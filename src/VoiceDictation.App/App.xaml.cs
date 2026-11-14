using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using VoiceDictation.App.Platform;
using VoiceDictation.App.UI;

namespace VoiceDictation.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;
    private DictationHost? _host;
    private TrayIcon? _tray;
    private GlobalHotkey? _pasteHotkey;
    private GlobalHotkey? _languageHotkey;
    private DictationKeyHook? _keyHook;
    private OverlayWindow? _overlay;
    private bool _recording;
    private int _recordingId;
    private bool _paused;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 2 && e.Args[0] == "--self-test")
        {
            Log.Initialize();
            _ = SelfTest.RunAsync(e.Args[1], e.Args.Length > 2 ? e.Args[2] : e.Args[1] + ".out.txt", Dispatcher)
                .ContinueWith(_ => Dispatcher.BeginInvoke(() => Shutdown()), TaskScheduler.Default);
            return;
        }

        _singleInstance = new Mutex(false, @"Local\VoiceDictation.SingleInstance");
        try
        {
            _ownsMutex = _singleInstance.WaitOne(TimeSpan.FromSeconds(3));
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        if (!_ownsMutex)
        {
            System.Windows.MessageBox.Show("Voice Dictation is already running — look for its icon in the system tray.", "Voice Dictation");
            Shutdown();
            return;
        }

        Log.Initialize();
        Log.Info($"starting {typeof(App).Assembly.GetName().Version} on {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        DispatcherUnhandledException += OnUnhandledException;

        var settings = AppSettings.LoadOrCreate();
        _overlay = new OverlayWindow();
        _overlay.Pinned = File.Exists(AppPaths.OverlayPinnedFlag);

        _host = new DictationHost(settings, Dispatcher);
        _tray = new TrayIcon(_host, settings, _overlay, SetPaused, () => Shutdown());
        _overlay.PinnedChanged += pinned => StateFlags.Set(AppPaths.OverlayPinnedFlag, pinned);
        _host.Status += _overlay.ShowStatus;
        _host.Activity += _overlay.ShowActivity;
        _host.DictationPreview += _overlay.ShowPreview;
        _host.InputLevel += _overlay.ShowInputLevel;
        _host.DictationLanguageChanged += _overlay.SetDictationLanguage;
        _host.CaptureStopped += id =>
        {
            // Recording couldn't start or stopped by itself: stop treating the keys (and Esc) as recording.
            if (id == _recordingId && _recording)
            {
                _recording = false;
                if (_keyHook is not null)
                {
                    _keyHook.IsRecording = false;
                }

                _tray?.SetRecording(false);
            }
        };
        _overlay.SetDictationLanguage(_host.HinglishDictation, _host.HinglishAccurate);

        _pasteHotkey = TryRegister(settings.PasteLastHotkey, _host.PasteLastTranscript, "paste last dictation");
        _languageHotkey = TryRegister(settings.DictationLanguageHotkey, _host.ToggleDictationLanguage, "switch dictation language");
        InstallDictationKeys();
        SetPaused(File.Exists(AppPaths.PausedFlag));
        WarnIfVoiceAssistantRuns();

        _host.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("exiting");
        _keyHook?.Dispose();
        _pasteHotkey?.Dispose();
        _languageHotkey?.Dispose();
        _host?.Dispose();
        _tray?.Dispose();
        if (_ownsMutex)
        {
            _singleInstance?.ReleaseMutex();
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void SetPaused(bool paused)
    {
        if (paused && _recording)
        {
            EndDictation(cancel: true);
        }

        _paused = paused;
        if (_keyHook is not null)
        {
            _keyHook.Enabled = !paused;
        }

        StateFlags.Set(AppPaths.PausedFlag, paused);
        _tray?.SetPaused(paused);
        _overlay?.SetPaused(paused);
        Log.Info(paused ? "dictation keys paused" : "dictation keys on");
    }

    /// <summary>Voice Assistant also listens for Ctrl+Win; with both running every dictation would be typed twice.</summary>
    private void WarnIfVoiceAssistantRuns()
    {
        var others = Process.GetProcessesByName("VoiceAssistant");
        var running = others.Length > 0;
        foreach (var process in others)
        {
            process.Dispose();
        }

        if (running)
        {
            Log.Warn("Voice Assistant is running too");
            _tray?.ShowBalloon("Voice Assistant is also running and uses the same Ctrl+Win keys, so text would be typed twice. Exit one of them.");
        }
    }

    /// <summary>Tap Ctrl+Win (or Ctrl+Win+Space) to start dictating, tap it again to finish; Esc cancels.</summary>
    private void InstallDictationKeys()
    {
        _keyHook = new DictationKeyHook(Dispatcher);
        if (!_keyHook.IsInstalled)
        {
            _tray?.ShowBalloon("Couldn't install the Ctrl+Win dictation keys.");
            return;
        }

        _keyHook.StartPressed += () =>
        {
            if (!_recording && !_paused)
            {
                BeginDictation();
            }
        };
        _keyHook.FinishPressed += () =>
        {
            if (_recording)
            {
                EndDictation(cancel: false);
            }
        };
        _keyHook.CancelPressed += () =>
        {
            if (_recording)
            {
                EndDictation(cancel: true);
            }
        };
    }

    private void BeginDictation()
    {
        _recording = true;
        _recordingId++;
        _keyHook!.IsRecording = true;
        _tray?.SetRecording(true);
        _host!.BeginDictationCapture(_recordingId);
    }

    private void EndDictation(bool cancel)
    {
        _recording = false;
        if (_keyHook is not null)
        {
            _keyHook.IsRecording = false;
        }

        _tray?.SetRecording(false);
        _host!.EndDictationCapture(cancel);
    }

    private GlobalHotkey? TryRegister(string chord, Action action, string purpose)
    {
        try
        {
            var hotkey = new GlobalHotkey(chord, action);
            if (!hotkey.IsRegistered)
            {
                _tray?.ShowBalloon($"{chord} ({purpose}) is used by another app.");
            }

            return hotkey;
        }
        catch (ArgumentException ex)
        {
            _tray?.ShowBalloon(ex.Message);
            return null;
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error($"unhandled UI exception: {e.Exception.GetType().Name} 0x{e.Exception.HResult:X8}");
        _tray?.ShowBalloon("Something went wrong: " + e.Exception.Message);
        e.Handled = true;
    }
}
