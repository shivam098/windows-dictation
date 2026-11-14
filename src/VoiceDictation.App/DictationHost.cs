using System.Threading.Channels;
using System.Windows.Threading;
using VoiceDictation.App.Platform;
using VoiceDictation.Core;
using VoiceDictation.Speech;

namespace VoiceDictation.App;

/// <summary>
/// Tap-to-dictate: the microphone opens when Ctrl+Win is tapped and closes when it is tapped again. The recording is
/// shown live (level meter and preview), then transcribed with Whisper and inserted at the cursor. All work runs on
/// one serial queue.
/// </summary>
internal sealed class DictationHost : IDisposable
{
    private static readonly HashSet<string> KnownHallucinations =
        ["you", "thanks for watching", "thank you for watching", "please subscribe"];

    private const int SampleRate = WhisperRecognizer.SampleRate;
    private const int PreviewIntervalMs = 600;
    private const int LevelIntervalMs = 50;

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
    private volatile WhisperRecognizer? _hinglishAccurate;
    private Task? _hinglishLoad;
    private Task? _hinglishAccurateLoad;
    private CancellationTokenSource? _accurateLoadCts;
    private volatile bool _hinglishDictation;
    private volatile bool _hinglishAccurateMode;
    private WhisperRecognizer? _captureRecognizer;
    private CancellationTokenSource? _previewCts;
    private Task? _previewTask;
    private Task? _queueTask;
    private volatile bool _capturing;
    private volatile int _captureId;
    private long _lastLevelTicks;
    private bool _limitReached;

    public DictationHost(AppSettings settings, Dispatcher ui, IDesktop? desktop = null, bool persistState = true)
    {
        _settings = settings;
        _ui = ui;
        _hinglishDictation = persistState && File.Exists(AppPaths.HinglishDictationFlag);
        _hinglishAccurateMode = persistState && File.Exists(AppPaths.HinglishAccurateFlag);
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
    /// start or stopped on its own (model still loading, microphone error, time limit), so the keys stop acting as if
    /// recording.
    /// </summary>
    public event Action<int>? CaptureStopped;

    /// <summary>
    /// Raised on the UI thread with a transient label (<see cref="DictationLabels.Listening"/>,
    /// <see cref="DictationLabels.Transcribing"/>); null clears it.
    /// </summary>
    public event Action<string?>? Activity;

    /// <summary>Raised on the UI thread about 20 times a second while recording with the microphone level (0 to 1).</summary>
    public event Action<float>? InputLevel;

    /// <summary>Raised on the UI thread with the live transcript while recording (null clears it).</summary>
    public event Action<string?>? DictationPreview;

    /// <summary>
    /// Raised on the UI thread when the dictation language changes: Hindi + English on/off, and whether it uses the
    /// accurate (slower) model.
    /// </summary>
    public event Action<bool, bool>? DictationLanguageChanged;

    public bool HinglishDictation => _hinglishDictation;

    public bool HinglishAccurate => _hinglishAccurateMode;

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

    /// <summary>Opens the microphone and starts recording. <paramref name="id"/> identifies this recording in <see cref="CaptureStopped"/>.</summary>
    public void BeginDictationCapture(int id) => Enqueue(() =>
    {
        if (_capturing)
        {
            return Task.CompletedTask;
        }

        _captureId = id;

        // A recording that ended early (cancelled, silence) may have kept a model that's no longer chosen.
        ReleaseAccurateIfUnused();
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

        _limitReached = false;
        _lastLevelTicks = 0;
        _capturing = true;
        _captureRecognizer = _recognizer;
        WhisperRecognizer? previewRecognizer = _recognizer;
        if (_hinglishDictation)
        {
            var final = HinglishForTyping();
            if (_hinglishAccurateMode && _hinglishAccurate is null)
            {
                // Retries after a failed download or load, too.
                EnsureHinglishLoading();
            }

            if (final is not null)
            {
                _captureRecognizer = final;

                // The accurate model takes seconds per pass: the live preview uses the fast one (or is skipped).
                previewRecognizer = final == _hinglishAccurate ? _hinglish : final;
                if (_hinglishAccurateMode && final != _hinglishAccurate)
                {
                    Report(StatusKind.Warning, "The accurate Hindi + English model is still loading; this dictation uses the fast one.");
                }
            }
            else
            {
                EnsureHinglishLoading();
                Report(StatusKind.Warning, "The Hindi + English model isn't ready yet; this dictation uses English.");
            }
        }

        _sounds.Play(SoundFeedback.Cue.StartTyping);
        PostActivity(DictationLabels.Listening);
        if (_settings.LivePreview && previewRecognizer is not null)
        {
            _previewCts = new CancellationTokenSource();
            var token = _previewCts.Token;
            var recognizer = previewRecognizer;
            _previewTask = Task.Run(() => RunPreviewAsync(recognizer, token));
        }

        return Task.CompletedTask;
    });

    /// <summary>Stops recording and closes the microphone; transcribes and types unless cancelled.</summary>
    public void EndDictationCapture(bool cancel) => Enqueue(() => EndCaptureAsync(cancel));

    /// <param name="cancel">Throw the recording away instead of typing it.</param>
    /// <param name="onlyId">Stop only if this recording is still the one in progress (time limit).</param>
    private async Task EndCaptureAsync(bool cancel, int? onlyId = null)
    {
        if (!_capturing || (onlyId is { } id && id != _captureId))
        {
            return;
        }

        _capturing = false;

        // Show “Transcribing…” straight away: stopping the live preview may wait for a pass that is still running.
        PostActivity(cancel ? null : DictationLabels.Transcribing);
        try
        {
            await StopPreviewAsync().ConfigureAwait(false);
            _microphone.Stop();
            float[] audio;
            lock (_captureGate)
            {
                audio = [.. _capture];
                _capture.Clear();
            }

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

            var parts = new List<string>();
            foreach (var chunk in chunks)
            {
                var result = await recognizer.TranscribeAsync(chunk, _shutdown.Token).ConfigureAwait(false);
                Log.Info($"chunk audio={chunk.Length * 1000 / SampleRate}ms asr={result.Elapsed.TotalMilliseconds:F0}ms{(recognizer == _hinglishAccurate ? " model=hinglish-accurate" : recognizer == _hinglish ? " model=hinglish" : string.Empty)}");
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

            // The language may have changed during this recording.
            ReleaseAccurateIfUnused();
        }
    }

    /// <summary>The recording reached <see cref="AppSettings.MaxDictationSeconds"/>: finish it as if Ctrl+Win was tapped.</summary>
    private async Task FinishAtLimitAsync(int id)
    {
        if (!_capturing || id != _captureId)
        {
            return;
        }

        var seconds = _settings.MaxDictationSeconds;
        Log.Info($"dictation reached the {seconds}s limit");
        ReportCaptureStopped(id);
        await EndCaptureAsync(cancel: false, onlyId: id).ConfigureAwait(false);
        var limit = seconds % 60 == 0 ? $"{seconds / 60} minute{(seconds == 60 ? string.Empty : "s")}" : $"{seconds} seconds";
        Report(StatusKind.Info, $"Dictation stops by itself after {limit}. Tap Ctrl+Win to keep going.");
    }
    public void PasteLastTranscript() => Enqueue(() =>
    {
        _inserter.RepeatLast();
        return Task.CompletedTask;
    });

    /// <summary>Switches dictation between English and Hindi + English (Alt+Shift+H), keeping the fast/accurate choice.</summary>
    public void ToggleDictationLanguage() => Enqueue(() =>
    {
        ApplyDictationLanguage(!_hinglishDictation, _hinglishAccurateMode);
        return Task.CompletedTask;
    });

    /// <summary>Sets the dictation language (tray menu); <paramref name="accurate"/> picks the larger Hindi + English model.</summary>
    public void SetDictationLanguage(bool hinglish, bool accurate = false) => Enqueue(() =>
    {
        if (hinglish != _hinglishDictation || (hinglish && accurate != _hinglishAccurateMode))
        {
            ApplyDictationLanguage(hinglish, hinglish ? accurate : _hinglishAccurateMode);
        }
        else if (hinglish && _recognizer is not null)
        {
            // Choosing the current mode again retries a model that failed to download or load.
            EnsureHinglishLoading();
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
        Task? hinglishAccurateLoad;
        lock (_hinglishGate)
        {
            hinglishLoad = _hinglishLoad;
            hinglishAccurateLoad = _hinglishAccurateLoad;
        }

        drained &= hinglishLoad?.Wait(TimeSpan.FromSeconds(5)) ?? true;
        drained &= hinglishAccurateLoad?.Wait(TimeSpan.FromSeconds(5)) ?? true;
        _microphone.Stop();
        _microphone.Dispose();
        _sounds.Dispose();
        if (drained)
        {
            _recognizer?.Dispose();
            _hinglish?.Dispose();
            _hinglishAccurate?.Dispose();
            _detector?.Dispose();
        }
        else
        {
            // Freeing a model while Whisper is mid-transcription crashes; the OS reclaims it at exit instead.
            Log.Warn("queue did not drain; leaving speech models to process exit");
        }
    }

    private void ApplyDictationLanguage(bool on, bool accurate)
    {
        _hinglishDictation = on;
        _hinglishAccurateMode = accurate;
        StateFlags.Set(AppPaths.HinglishDictationFlag, on);
        StateFlags.Set(AppPaths.HinglishAccurateFlag, accurate);
        Log.Info($"dictation language: {(on ? accurate ? "hinglish-accurate" : "hinglish" : "english")}");
        Post(() => DictationLanguageChanged?.Invoke(on, accurate));
        if (!accurate)
        {
            // Not wanted any more: stop its download/load and free its memory (about 1 GB).
            _accurateLoadCts?.Cancel();
            ReleaseAccurateIfUnused();
        }

        if (!on)
        {
            Report(StatusKind.Info, "Dictation language: English.");
        }
        else if (HinglishForTyping() is { } ready && (!accurate || ready == _hinglishAccurate))
        {
            Report(StatusKind.Info, accurate
                ? "Dictation language: Hindi + English, accurate (slower; Hindi is typed in Roman letters)."
                : "Dictation language: Hindi + English (Hindi is typed in Roman letters).");
        }
        else if (_recognizer is not null)
        {
            EnsureHinglishLoading();
        }
    }

    /// <summary>The model that types Hindi + English text: the accurate one when chosen and loaded, else the fast one.</summary>
    private WhisperRecognizer? HinglishForTyping() =>
        _hinglishAccurateMode && _hinglishAccurate is { } accurate ? accurate : _hinglish;

    /// <summary>
    /// Frees the accurate model when that mode is no longer chosen. Runs on the work queue, where every use of it
    /// happens; a recording that started with it keeps it until it has been transcribed.
    /// </summary>
    private void ReleaseAccurateIfUnused()
    {
        if (!_hinglishAccurateMode && !_capturing && _hinglishAccurate is { } model)
        {
            _hinglishAccurate = null;
            model.Dispose();
            Log.Info("hinglish-accurate model unloaded");
        }
    }

    private void OnSamples(float[] samples)
    {
        if (!_capturing)
        {
            return;
        }

        int recorded;
        lock (_captureGate)
        {
            _capture.AddRange(samples);
            recorded = _capture.Count;
        }

        var now = Environment.TickCount64;
        if (now - _lastLevelTicks >= LevelIntervalMs)
        {
            _lastLevelTicks = now;
            var level = AudioLevel.Of(samples);
            Post(() => InputLevel?.Invoke(level));
        }

        var max = _settings.MaxDictationSeconds;
        if (max > 0 && !_limitReached && recorded >= (long)max * SampleRate)
        {
            _limitReached = true;
            var id = _captureId;
            Enqueue(() => FinishAtLimitAsync(id));
        }
    }

    /// <summary>Makes sure a model file exists, downloading and verifying it on first use.</summary>
    private async Task<bool> EnsureModelAsync(string file, bool firstRun = true, string? what = null, CancellationToken cancellationToken = default)
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
        what ??= "Hindi + English model (about 80 MB";
        Report(StatusKind.Info, firstRun
            ? "First run: downloading the speech model (one time, stays on this PC)…"
            : $"Downloading the {what}, one time)…");
        try
        {
            // Progress shows in the pill only on first run; later downloads must not replace a "Listening…" label,
            // so they report every 10% as a status message instead.
            var lastTenth = 0;
            await ModelDownloader.DownloadAsync(file, AppPaths.ModelsDir, percent =>
            {
                if (firstRun)
                {
                    PostActivity($"Downloading speech model… {percent}%");
                }
                else if (percent / 10 > lastTenth && percent < 100)
                {
                    lastTenth = percent / 10;
                    Report(StatusKind.Info, $"Downloading the {what.Split(" (")[0]}… {percent}%");
                }
            }, cancellationToken.CanBeCanceled ? cancellationToken : _shutdown.Token).ConfigureAwait(false);
            Log.Info($"downloaded {file}");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            Log.Info($"download of {file} cancelled");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
        {
            Log.Error($"download of {file} failed: {ex.GetType().Name}");
            Report(StatusKind.Error, firstRun
                ? "Couldn't download the speech model. Check the internet connection and restart Voice Dictation."
                : $"Couldn't download the {what.Split(" (")[0]}. Check the internet connection and switch it on again.");
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

        Report(StatusKind.Info, "Ready. Tap Ctrl+Win, speak, then tap it again to type.");
    }

    /// <summary>
    /// Downloads (first time) and loads the Hindi + English model(s) in the background. The accurate mode loads both:
    /// the fast model drives the live preview.
    /// </summary>
    private void EnsureHinglishLoading()
    {
        lock (_hinglishGate)
        {
            if (_hinglish is null && _hinglishLoad is null)
            {
                _hinglishLoad = Task.Run(() => LoadHinglishAsync(accurate: false));
            }

            if (_hinglishAccurateMode && _hinglishAccurate is null && _hinglishAccurateLoad is null)
            {
                _accurateLoadCts?.Dispose();
                _accurateLoadCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                var token = _accurateLoadCts.Token;
                _hinglishAccurateLoad = Task.Run(() => LoadHinglishAsync(accurate: true, token));
            }
        }
    }

    private async Task LoadHinglishAsync(bool accurate, CancellationToken cancellationToken = default)
    {
        var file = accurate ? _settings.HinglishAccurateModelFile : _settings.HinglishModelFile;
        var what = accurate ? "accurate Hindi + English model (about 830 MB" : "Hindi + English model (about 80 MB";
        var token = cancellationToken.CanBeCanceled ? cancellationToken : _shutdown.Token;
        try
        {
            if (!await EnsureModelAsync(file, firstRun: false, what, token).ConfigureAwait(false))
            {
                return;
            }

            var started = DateTime.UtcNow;
            var recognizer = new WhisperRecognizer(AppPaths.FindModel(file)!, new RecognizerOptions
            {
                Threads = _settings.Threads,
                AudioContextSize = _settings.AudioContextSize,
            });
            try
            {
                // Not cancellable: Whisper keeps running natively after a cancel, so it must finish before Dispose.
                await recognizer.TranscribeAsync(new float[SampleRate], CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                recognizer.Dispose();
                throw;
            }

            if (token.IsCancellationRequested || (accurate && !_hinglishAccurateMode))
            {
                // Switched to another mode (or exiting) while it loaded.
                recognizer.Dispose();
                Log.Info($"{(accurate ? "hinglish-accurate" : "hinglish")} model no longer needed after loading; unloaded");
                return;
            }

            if (accurate)
            {
                _hinglishAccurate = recognizer;
            }
            else
            {
                _hinglish = recognizer;
            }

            Log.Info($"{(accurate ? "hinglish-accurate" : "hinglish")} model loaded in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
            if (_hinglishDictation && accurate == _hinglishAccurateMode)
            {
                Report(StatusKind.Success, accurate
                    ? "Accurate Hindi + English dictation is ready (a few seconds per phrase; Hindi is typed in Roman letters)."
                    : "Hindi + English dictation is ready (Hindi is typed in Roman letters).");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"{(accurate ? "hinglish-accurate" : "hinglish")} model load failed: {ex.GetType().Name}");
            Report(StatusKind.Error, $"Couldn't load the {what.Split(" (")[0]}. Delete it from the models folder and switch it on again.");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_hinglishGate)
            {
                if (accurate)
                {
                    _hinglishAccurateLoad = null;
                }
                else
                {
                    _hinglishLoad = null;
                }
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
