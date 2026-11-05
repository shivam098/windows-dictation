using Whisper.net;

namespace VoiceDictation.Speech;

/// <summary>
/// Neural voice activity detection (Silero, via whisper.cpp). Distinguishes speech from music and noise,
/// which an energy threshold cannot do. Used to drop non-speech utterances and to split utterances that
/// background sound merged together.
/// </summary>
public sealed class SpeechDetector : IDisposable
{
    private readonly WhisperVadFactory _factory;
    private readonly WhisperVadProcessor _processor;
    private readonly object _gate = new();

    public SpeechDetector(string modelPath, float threshold = 0.5f)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("Voice activity model not found.", modelPath);
        }

        _factory = WhisperVadFactory.FromPath(modelPath);
        _processor = _factory.CreateBuilder()
            .WithThreads(2)
            .WithThreshold(threshold)
            .WithMinSpeechDuration(TimeSpan.FromMilliseconds(150))
            .WithMinSilenceDuration(TimeSpan.FromMilliseconds(250))
            .WithSpeechPadding(TimeSpan.Zero)
            .Build();
    }

    /// <summary>Returns speech regions in the samples (16 kHz mono), in order.</summary>
    public IReadOnlyList<(TimeSpan Start, TimeSpan End)> Detect(float[] samples)
    {
        lock (_gate)
        {
            return _processor.DetectSpeech(samples).Select(s => (s.Start, s.End)).ToList();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _processor.Dispose();
            _factory.Dispose();
        }
    }
}
