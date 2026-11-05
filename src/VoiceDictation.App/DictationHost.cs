using System.Threading.Channels;
using System.Windows.Threading;
using VoiceDictation.App.Platform;
using VoiceDictation.Core;
using VoiceDictation.Speech;

namespace VoiceDictation.App;

/// <summary>
/// Push-to-talk dictation: the microphone opens only while the keys are held (or hands-free is on), the recording is
/// shown live, then transcribed with Whisper and inserted at the cursor. All work runs on one serial queue.
/// </summary>
internal sealed class DictationHost : IDisposable
{
    private static readonly HashSet<string> KnownHallucinations =
        ["you", "thanks for watching", "thank you for watching", "please subscribe"];

    private const int SampleRate = WhisperRecognizer.SampleRate;
    private const int PreviewIntervalMs = 600;

    private readonly AppSettings _settings;
    private readonly Dispatcher _ui;
    private readonly Channel<Func<Task>> _queue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _captureGate = new();
    private readonly List<float> _capture = [];
    private readonly MicrophoneCapture _microphone;
    private readonly DictationFormatter _formatter;
    private readonly DictationInserter _inserter;
    private readonly SoundFeedback _sounds;
    private readonly object _hinglishGate = new();
    private WhisperRecognizer? _recognizer;
    private SpeechDetector? _detector;
    private volatile WhisperRecognizer? _hinglish;
    private Task? _hinglishLoad;
    private volatile bool _hinglishDictation;
    private WhisperRecognizer? _captureRecognizer;
    private CancellationTokenSource? _previewCts;
    private Task? _previewTask;
    private Task? _queueTask;
    private volatile bool _capturing;
    private int _captureId;

    public DictationHost(AppSettings settings, Dispatcher ui, IDesktop? desktop = null, bool persistState = true)
    {
        _settings = settings;
        _ui = ui;
        _hinglishDictation = persistState && File.Exists(AppPaths.HinglishDictationFlag);
        WindowsDesktop.InsertMethod = settings.InsertMethod;
        _sounds = new SoundFeedback(settings.SoundFeedback && persistState);
        _formatter = new DictationFormatter(new DictationOptions
        {
            RemoveFillerWords = settings.RemoveFillerWords,
            SpokenPunctuation = settings.SpokenPunctuation,
            Replacements = settings.Replacements,
            Snippets = settings.Snippets,
        });
        _inserter = new DictationInserter(desktop ?? new WindowsDesktop(), _formatter, settings.NewLineKeys);
        _inserter.Status += update => Post(() => Status?.Invoke(update));

        _microphone = new MicrophoneCapture(settings.MicrophoneDevice);
        _microphone.Samples += OnSamples;
        _microphone.Failed += ex =>
        {
            Log.Error($"microphone failed: {ex.GetType().Name}");
            Enqueue(async () =>
            {
                var wasCapturing = _capturing;
                await StopPreviewAsync().ConfigureAwait(false);
                _capturing = false;
                _microphone.Stop();
                PostActivity(null);
                if (wasCapturing)
                {
                    ReportCaptureStopped(_captureId);
                }

                Report(StatusKind.Error, "The microphone stopped. Check it's connected, then try again.");
            });
        };
    }

    /// <summary>Raised on the UI thread.</summary>
    public event Action<StatusUpdate>? Status;

    /// <summary>
    /// Raised on the UI thread with the id passed to <see cref="BeginDictationCapture"/> when that recording couldn't
    /// start or stopped on its own (model still loading, microphone error), so the keys stop acting as if recording.
    /// </summary>
    public event Action<int>? CaptureStopped;

    /// <summary>Raised on the UI thread with a transient label (“Dictating…”, “Transcribing…”); null clears it.</summary>
    public event Action<string?>? Activity;

    /// <summary>Raised on the UI thread with the live transcript while recording (null clears it).</summary>
    public event Action<string?>? DictationPreview;

    /// <summary>Raised on the UI thread when dictation switches between English (false) and Hindi + English (true).</summary>
    public event Action<bool>? DictationLanguageChanged;

    public bool HinglishDictation => _hinglishDictation;

    /// <summary>For scripted self-tests (call only from <see cref="RunOnQueueAsync"/>).</summary>
    internal DictationInserter Inserter => _inserter;

    public void Start()
    {
        _queueTask = Task.Run(ProcessQueueAsync);
        Enqueue(LoadAsync);
    }

    /// <summary>Starts only the queue (no microphone or models) for scripted self-tests.</summary>
    internal void StartForSelfTest() => _queueTask = Task.Run(ProcessQueueAsync);

    internal Task RunOnQueueAsync(Func<Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    /// <summary>Changes the label of the recording in progress (hold → hands-free); never starts recording.</summary>
    public void UpdateCaptureLabel(string label) => Enqueue(() =>
    {
        if (_capturing)
        {
            PostActivity(label);
        }

        return Task.CompletedTask;
    });

    /// <summary>Opens the microphone and starts recording. <paramref name="id"/> identifies this recording in <see cref="CaptureStopped"/>.</summary>
    public void BeginDictationCapture(string label, int id) => Enqueue(() =>
    {
        if (_capturing)
        {
            return Task.CompletedTask;
        }

        _captureId = id;
        if (_recognizer is null)
        {
            Report(StatusKind.Warning, "Still loading the speech model…");
            ReportCaptureStopped(id);
            return Task.CompletedTask;
        }

        lock (_captureGate)
        {
            _capture.Clear();
        }

        try
        {
            _microphone.Start();
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            Log.Error($"microphone start failed: {ex.GetType().Name}");
            Report(StatusKind.Error, "Couldn't open the microphone. Check Settings › Privacy › Microphone.");
            ReportCaptureStopped(id);
            return Task.CompletedTask;
        }

        _capturing = true;
        _captureRecognizer = _recognizer;
        if (_hinglishDictation)
        {
            if (_hinglish is not null)
            {
                _captureRecognizer = _hinglish;
            }
            else
            {
                EnsureHinglishLoading();
                Report(StatusKind.Warning, "The Hindi + English model isn't ready yet; this dictation uses English.");
            }
        }

        _sounds.Play(SoundFeedback.Cue.StartTyping);
        PostActivity(label);
        if (_settings.LivePreview)
        {
            _previewCts = new CancellationTokenSource();
            var token = _previewCts.Token;
            var recognizer = _captureRecognizer;
            _previewTask = Task.Run(() => RunPreviewAsync(recognizer, token));
        }

        return Task.CompletedTask;
    });

    /// <summary>Stops recording and closes the microphone; transcribes and types unless cancelled.</summary>
    public void EndDictationCapture(bool cancel) => Enqueue(async () =>
    {
        if (!_capturing)
        {
            return;
        }

        _capturing = false;
        await StopPreviewAsync().ConfigureAwait(false);
        _microphone.Stop();
        float[] audio;
        lock (_captureGate)
        {
            audio = [.. _capture];
            _capture.Clear();
        }

        PostActivity(null);
        _sounds.Play(cancel ? SoundFeedback.Cue.Standby : SoundFeedback.Cue.StopTyping);
        if (cancel)
        {
            Report(StatusKind.Info, "Dictation cancelled.");
            return;
        }

        var recognizer = _captureRecognizer ?? _recognizer;
        if (audio.Length < SampleRate / 4 || recognizer is null)
        {
            return;
        }

        IReadOnlyList<float[]> chunks = _detector is null
            ? SpeechSegments.Chunk(audio, [(TimeSpan.Zero, TimeSpan.FromSeconds((double)audio.Length / SampleRate))], SampleRate, 14000, 0)
            : SpeechSegments.Chunk(audio, _detector.Detect(audio), SampleRate, 14000, 200);
        if (chunks.Count == 0)
        {
            Report(StatusKind.Info, "I didn't hear any speech.");
            return;
        }

        PostActivity("Transcribing…");
        try
        {
            var parts = new List<string>();
            foreach (var chunk in chunks)
            {
                var result = await recognizer.TranscribeAsync(chunk, _shutdown.Token).ConfigureAwait(false);
                Log.Info($"chunk audio={chunk.Length * 1000 / SampleRate}ms asr={result.Elapsed.TotalMilliseconds:F0}ms{(recognizer == _hinglish ? " model=hinglish" : string.Empty)}");
                var text = TranscriptText.Clean(result.Text);
                if (!(chunk.Length < SampleRate * 5 / 2 && KnownHallucinations.Contains(TranscriptText.Normalize(text))))
                {
                    parts.Add(text);
                }
            }

            _inserter.Insert(string.Join(" ", parts.Where(p => p.Length > 0)));
        }
        finally
        {
            PostActivity(null);
        }
    });

    public void PasteLastTranscript() => Enqueue(() =>
    {
        _inserter.RepeatLast();
        return Task.CompletedTask;
    });

    /// <summary>Switches dictation between English and Hindi + English (Alt+Shift+H).</summary>
    public void ToggleDictationLanguage() => Enqueue(() =>
    {
        ApplyDictationLanguage(!_hinglishDictation);
        return Task.CompletedTask;
    });

    /// <summary>Sets the dictation language (tray menu); does nothing if it is already that language.</summary>
    public void SetDictationLanguage(bool hinglish) => Enqueue(() =>
    {
        if (hinglish != _hinglishDictation)
        {
            ApplyDictationLanguage(hinglish);
        }

        return Task.CompletedTask;
    });

    public void Dispose()
    {
        _previewCts?.Cancel();
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        var drained = true;
        try
        {
            drained = _queueTask?.Wait(TimeSpan.FromSeconds(5)) ?? true;
        }
        catch (AggregateException ex)
        {
            Log.Warn($"queue ended with {ex.InnerException?.GetType().Name}");
        }

        drained &= _previewTask?.Wait(TimeSpan.FromSeconds(5)) ?? true;
        Task? hinglishLoad;
        lock (_hinglishGate)
        {
            hinglishLoad = _hinglishLoad;
        }

        drained &= hinglishLoad?.Wait(TimeSpan.FromSeconds(5)) ?? true;
        _microphone.Stop();
        _microphone.Dispose();
        _sounds.Dispose();
        if (drained)
        {
            _recognizer?.Dispose();
            _hinglish?.Dispose();
            _detector?.Dispose();
        }
        else
        {
            // Freeing a model while Whisper is mid-transcription crashes; the OS reclaims it at exit instead.
            Log.Warn("queue did not drain; leaving speech models to process exit");
        }
    }

    private void ApplyDictationLanguage(bool on)
    {
        _hinglishDictation = on;
        StateFlags.Set(AppPaths.HinglishDictationFlag, on);
        Log.Info($"dictation language: {(on ? "hinglish" : "english")}");
        Post(() => DictationLanguageChanged?.Invoke(on));
        if (!on)
        {
            Report(StatusKind.Info, "Dictation language: English.");
        }
        else if (_hinglish is not null)
        {
            Report(StatusKind.Info, "Dictation language: Hindi + English (Hindi is typed in Roman letters).");
        }
        else if (_recognizer is not null)
        {
            EnsureHinglishLoading();
        }
    }

    private void OnSamples(float[] samples)
    {
        if (_capturing)
        {
            lock (_captureGate)
            {
                _capture.AddRange(samples);
            }
        }
    }

    /// <summary>Makes sure a model file exists, downloading and verifying it on first use.</summary>
    private async Task<bool> EnsureModelAsync(string file, bool firstRun = true)
    {
        if (AppPaths.FindModel(file) is not null)
        {
            return true;
        }

        if (!ModelDownloader.CanDownload(file))
        {
            Report(StatusKind.Error, $"Speech model {file} not found in {AppPaths.ModelsDir}.");
            return false;
        }

        Log.Info($"downloading {file}");
        Report(StatusKind.Info, firstRun
            ? "First run: downloading the speech model (one time, stays on this PC)…"
            : "Downloading the Hindi + English model (about 80 MB, one time)…");
        try
        {
            // Progress shows in the pill only on first run; later downloads must not replace a "Dictating…" label.
            await ModelDownloader.DownloadAsync(file, AppPaths.ModelsDir, percent =>
            {
                if (firstRun)
                {
                    PostActivity($"Downloading speech model… {percent}%");
                }
            }, _shutdown.Token).ConfigureAwait(false);
            Log.Info($"downloaded {file}");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
        {
            Log.Error($"download of {file} failed: {ex.GetType().Name}");
            Report(StatusKind.Error, firstRun
                ? "Couldn't download the speech model. Check the internet connection and restart Voice Dictation."
                : "Couldn't download the Hindi + English model. Check the internet connection and switch it on again.");
            return false;
        }
        finally
        {
            if (firstRun)
            {
                PostActivity(null);
            }
        }
    }

    private async Task LoadAsync()
    {
        foreach (var file in new[] { _settings.ModelFile, _settings.VadModelFile })
        {
            if (!await EnsureModelAsync(file).ConfigureAwait(false) && file == _settings.ModelFile)
            {
                return;
            }
        }

        PostActivity("Loading speech model…");
        var started = DateTime.UtcNow;
        try
        {
            var modelPath = AppPaths.FindModel(_settings.ModelFile)!;
            _recognizer = await Task.Run(() => new WhisperRecognizer(modelPath, new RecognizerOptions
            {
                Threads = _settings.Threads,
                AudioContextSize = _settings.AudioContextSize,
                Prompt = _settings.VocabularyPrompt,
            })).ConfigureAwait(false);
            await _recognizer.TranscribeAsync(new float[SampleRate], _shutdown.Token).ConfigureAwait(false);

            if (AppPaths.FindModel(_settings.VadModelFile) is { } vadPath)
            {
                _detector = new SpeechDetector(vadPath);
            }
            else
            {
                Log.Warn("vad model missing; transcribing whole recordings");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"model load failed: {ex.GetType().Name}");
            Report(StatusKind.Error, "Couldn't load the speech model. Delete it from the models folder and restart to re-download.");
            return;
        }
        finally
        {
            PostActivity(null);
        }

        Log.Info($"models loaded in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms (vad={_detector is not null})");
        if (_hinglishDictation)
        {
            EnsureHinglishLoading();
        }

        Report(StatusKind.Info, "Ready. Hold Ctrl+Win and speak.");
    }

    /// <summary>Downloads (first time) and loads the Hindi + English model in the background.</summary>
    private void EnsureHinglishLoading()
    {
        lock (_hinglishGate)
        {
            if (_hinglish is null && _hinglishLoad is null)
            {
                _hinglishLoad = Task.Run(LoadHinglishAsync);
            }
        }
    }

    private async Task LoadHinglishAsync()
    {
        try
        {
            if (!await EnsureModelAsync(_settings.HinglishModelFile, firstRun: false).ConfigureAwait(false))
            {
                return;
            }

            var started = DateTime.UtcNow;
            var recognizer = new WhisperRecognizer(AppPaths.FindModel(_settings.HinglishModelFile)!, new RecognizerOptions
            {
                Threads = _settings.Threads,
                AudioContextSize = _settings.AudioContextSize,
            });
            await recognizer.TranscribeAsync(new float[SampleRate], _shutdown.Token).ConfigureAwait(false);
            _hinglish = recognizer;
            Log.Info($"hinglish model loaded in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
            if (_hinglishDictation)
            {
                Report(StatusKind.Success, "Hindi + English dictation is ready (Hindi is typed in Roman letters).");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"hinglish model load failed: {ex.GetType().Name}");
            Report(StatusKind.Error, "Couldn't load the Hindi + English model. Delete it from the models folder and switch it on again.");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_hinglishGate)
            {
                _hinglishLoad = null;
            }
        }
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await work().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error($"action failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
                Report(StatusKind.Error, "That didn't work: " + ex.Message);
            }
        }
    }

    private void Enqueue(Func<Task> work) => _queue.Writer.TryWrite(work);

    /// <summary>Re-transcribes the recording so far every <see cref="PreviewIntervalMs"/> and shows it live.</summary>
    private async Task RunPreviewAsync(WhisperRecognizer recognizer, CancellationToken token)
    {
        var detector = _detector;
        var preview = new DictationPreview(
            async audio =>
            {
                // Never cancel Whisper mid-run (it keeps running natively); the final transcription waits for it.
                var result = await recognizer.TranscribeAsync(audio, CancellationToken.None).ConfigureAwait(false);
                var text = TranscriptText.Clean(result.Text);
                return audio.Length < SampleRate * 5 / 2 && KnownHallucinations.Contains(TranscriptText.Normalize(text)) ? string.Empty : text;
            },
            detector is null ? null : detector.Detect,
            SampleRate);
        var seen = 0;
        var updates = 0;
        var busy = TimeSpan.Zero;
        string? shown = null;
        try
        {
            while (true)
            {
                await Task.Delay(PreviewIntervalMs, token).ConfigureAwait(false);
                float[] pending;
                lock (_captureGate)
                {
                    if (_capture.Count - seen < SampleRate * 3 / 10)
                    {
                        continue;
                    }

                    seen = _capture.Count;
                    pending = _capture.GetRange(preview.CommittedSamples, seen - preview.CommittedSamples).ToArray();
                }

                if (pending.Length < SampleRate / 2)
                {
                    continue;
                }

                var started = DateTime.UtcNow;
                var text = await preview.UpdateAsync(pending).ConfigureAwait(false);
                busy += DateTime.UtcNow - started;
                updates++;
                var formatted = text.Length == 0 ? string.Empty : _formatter.Format(text).Replace("\r", string.Empty).Replace("\n", " ↵ ");
                if (!token.IsCancellationRequested && formatted != shown)
                {
                    shown = formatted;
                    Post(() => DictationPreview?.Invoke(formatted));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"live preview stopped: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
        finally
        {
            if (updates > 0)
            {
                Log.Info($"live preview updates={updates} avg={busy.TotalMilliseconds / updates:F0}ms");
            }
        }
    }

    /// <summary>Stops the live preview and waits for any in-flight transcription so the recognizer is free.</summary>
    private async Task StopPreviewAsync()
    {
        var task = _previewTask;
        _previewCts?.Cancel();
        if (task is not null)
        {
            await task.ConfigureAwait(false);
        }

        _previewCts = null;
        _previewTask = null;
        Post(() => DictationPreview?.Invoke(null));
    }

    private void ReportCaptureStopped(int id) => Post(() => CaptureStopped?.Invoke(id));

    private void Report(StatusKind kind, string message) => Post(() => Status?.Invoke(new StatusUpdate(kind, message)));

    private void PostActivity(string? activity) => Post(() => Activity?.Invoke(activity));

    private void Post(Action action)
    {
        if (!_ui.HasShutdownStarted)
        {
            _ui.BeginInvoke(action);
        }
    }
}
