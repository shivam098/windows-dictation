using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using VoiceDictation.App.Platform;

namespace VoiceDictation.App.UI;

internal sealed class TrayIcon : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "VoiceDictation";

    private readonly NotifyIcon _icon;
    private readonly (Icon Icon, nint Handle) _readyIcon;
    private readonly (Icon Icon, nint Handle) _recordingIcon;
    private readonly (Icon Icon, nint Handle) _pausedIcon;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _pause;
    private readonly ToolStripMenuItem _startAtSignIn;
    private bool _paused;
    private bool _recording;

    public TrayIcon(DictationHost host, AppSettings settings, OverlayWindow overlay, Action<bool> setPaused, Action exit)
    {
        _readyIcon = MakeIcon(null);
        _recordingIcon = MakeIcon(Color.FromArgb(234, 67, 53));
        _pausedIcon = MakeIcon(Color.FromArgb(154, 160, 166));

        _status = new ToolStripMenuItem("Starting…") { Enabled = false };
        _pause = new ToolStripMenuItem("Pause dictation keys", null, (_, _) => setPaused(!_paused));
        _startAtSignIn = new ToolStripMenuItem("Start when I sign in", null, (_, _) => ToggleStartAtSignIn()) { Checked = IsStartAtSignIn() };
        var showPill = new ToolStripMenuItem("Always show status pill", null, (_, _) => overlay.Pinned = !overlay.Pinned);
        var english = new ToolStripMenuItem("English", null, (_, _) => host.SetDictationLanguage(hinglish: false));
        var hinglish = new ToolStripMenuItem("Hindi + English — fast (typed in Roman letters)", null, (_, _) => host.SetDictationLanguage(hinglish: true, accurate: false));
        var hinglishAccurate = new ToolStripMenuItem("Hindi + English — accurate (slower; 830 MB download)", null, (_, _) => host.SetDictationLanguage(hinglish: true, accurate: true));

        // The hotkey switches between English and whichever Hindi + English mode was chosen last.
        var language = new ToolStripMenuItem($"Dictation language\t{settings.DictationLanguageHotkey}");
        language.DropDownItems.AddRange([english, hinglish, hinglishAccurate]);

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            showPill.Checked = overlay.Pinned;
            english.Checked = !host.HinglishDictation;
            hinglish.Checked = host.HinglishDictation && !host.HinglishAccurate;
            hinglishAccurate.Checked = host.HinglishDictation && host.HinglishAccurate;
        };
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem($"Paste last dictation\t{settings.PasteLastHotkey}", null, (_, _) => host.PasteLastTranscript()));
        menu.Items.Add(language);
        menu.Items.Add(_pause);
        menu.Items.Add(new ToolStripMenuItem("How to use…", null, (_, _) => ShowHelp(settings)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(showPill);
        menu.Items.Add(_startAtSignIn);
        menu.Items.Add(new ToolStripMenuItem("Open settings file", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("Open logs folder", null, (_, _) => Open(AppPaths.LogsDir)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => exit()));

        _icon = new NotifyIcon
        {
            Icon = _readyIcon.Icon,
            Text = "Voice Dictation",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                overlay.Pinned = !overlay.Pinned;
            }
        };
        Refresh();
    }

    public void SetRecording(bool recording)
    {
        _recording = recording;
        Refresh();
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        Refresh();
    }

    public void ShowBalloon(string message, bool warning = true) =>
        _icon.ShowBalloonTip(4000, "Voice Dictation", message, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var (icon, handle) in new[] { _readyIcon, _recordingIcon, _pausedIcon })
        {
            icon.Dispose();
            Win32.DestroyIcon(handle);
        }
    }

    private void Refresh()
    {
        var (icon, label) = _recording ? (_recordingIcon.Icon, "Recording — tap Ctrl+Win to finish")
            : _paused ? (_pausedIcon.Icon, "Paused (dictation keys off)")
            : (_readyIcon.Icon, "Ready — tap Ctrl+Win to dictate");
        _icon.Icon = icon;
        _icon.Text = $"Voice Dictation — {label}";
        _status.Text = label;
        _pause.Text = _paused ? "Resume dictation keys" : "Pause dictation keys";
    }

    /// <summary>The app logo with a status dot in the corner (no dot when ready).</summary>
    private static (Icon, nint) MakeIcon(Color? dot)
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        using var logo = new Icon(stream, 32, 32);
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.DrawIcon(logo, new Rectangle(0, 0, 32, 32));
            if (dot is { } color)
            {
                using var ring = new SolidBrush(Color.White);
                g.FillEllipse(ring, 17, 17, 15, 15);
                using var fill = new SolidBrush(color);
                g.FillEllipse(fill, 19, 19, 11, 11);
            }
        }

        var handle = bitmap.GetHicon();
        return (Icon.FromHandle(handle), handle);
    }

    private static void ShowHelp(AppSettings settings) => System.Windows.MessageBox.Show(
        $"""
        DICTATE INTO ANY APP
          Tap Ctrl+Win (press and let go) → recording starts; no need to hold the keys.
          Speak: the pill shows your voice level and the words as you say them.
          Tap Ctrl+Win again → “Transcribing…”, then the text appears where your cursor is.
          Ctrl+Win+Space works the same way. Esc while recording → cancel.
          {settings.PasteLastHotkey} → paste the last dictation again.
          {settings.DictationLanguageHotkey} → switch between English and Hindi + English
          (Hindi is typed in Roman letters).

        WHILE YOU SPEAK
          Say “comma”, “full stop”, “question mark”, “new line”, “new paragraph”.
          “um” and “uh” are removed. “…scratch that…” drops the phrase before it.

        PRIVATE BY DESIGN
          The microphone is on only from one tap of Ctrl+Win to the next{LimitNote(settings)}.
          Speech is turned into text on this PC; nothing is sent anywhere.
          Password fields and administrator windows are never typed into.

        Personal dictionary, snippets and new-line keys per app: tray › Open settings file.
        """,
        "Voice Dictation — how to use");

    private static string LimitNote(AppSettings settings) => settings.MaxDictationSeconds <= 0
        ? string.Empty
        : settings.MaxDictationSeconds % 60 == 0
            ? $"\n  (a recording stops by itself after {settings.MaxDictationSeconds / 60} min)"
            : $"\n  (a recording stops by itself after {settings.MaxDictationSeconds} s)";

    private static void OpenSettings()
    {
        if (!File.Exists(AppPaths.SettingsFile))
        {
            new AppSettings().Save();
        }

        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppPaths.SettingsFile}\"") { UseShellExecute = true })?.Dispose();
        System.Windows.MessageBox.Show("Restart Voice Dictation after saving changes.", "Voice Dictation");
    }

    private static void Open(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
    }

    private static bool IsStartAtSignIn()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    private void ToggleStartAtSignIn()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (IsStartAtSignIn())
        {
            key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        }

        _startAtSignIn.Checked = IsStartAtSignIn();
    }
}
