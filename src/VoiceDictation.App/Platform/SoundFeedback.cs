using NAudio.Wave;

namespace VoiceDictation.App.Platform;

/// <summary>
/// Short, quiet tones (like Wispr's start/stop sounds) so the user knows they were heard.
/// Pure tones aren't speech, so the voice-activity model drops them if the microphone picks them up.
/// </summary>
internal sealed class SoundFeedback : IDisposable
{
    private readonly bool _enabled;
    private readonly object _gate = new();
    private WaveOutEvent? _output;

    public SoundFeedback(bool enabled)
    {
        _enabled = enabled;
    }

    public enum Cue
    {
        Wake,
        StartTyping,
        StopTyping,
        Standby,
    }

    public void Play(Cue cue)
    {
        if (!_enabled)
        {
            return;
        }

        var notes = cue switch
        {
            Cue.Wake => new[] { (660.0, 70), (880.0, 90) },
            Cue.StartTyping => [(880.0, 110)],
            Cue.StopTyping => [(880.0, 70), (660.0, 90)],
            _ => [(520.0, 80)],
        };

        try
        {
            var samples = Render(notes);
            var provider = new RawSourceWaveStream(ToBytes(samples), 0, samples.Length * 2, new WaveFormat(22050, 16, 1));
            lock (_gate)
            {
                _output?.Dispose();
                _output = new WaveOutEvent { DesiredLatency = 60 };
                _output.Init(provider);
                _output.Play();
            }
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            Log.Warn($"sound feedback failed: {ex.GetType().Name}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _output?.Dispose();
            _output = null;
        }
    }

    private static short[] Render((double Hz, int Ms)[] notes)
    {
        const int rate = 22050;
        var result = new List<short>();
        foreach (var (hz, ms) in notes)
        {
            var count = rate * ms / 1000;
            for (var i = 0; i < count; i++)
            {
                // Short fade in/out avoids clicks; amplitude kept low.
                var envelope = Math.Min(1.0, Math.Min(i, count - i) / (rate * 0.008));
                result.Add((short)(Math.Sin(2 * Math.PI * hz * i / rate) * envelope * 0.12 * short.MaxValue));
            }
        }

        return [.. result];
    }

    private static byte[] ToBytes(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
