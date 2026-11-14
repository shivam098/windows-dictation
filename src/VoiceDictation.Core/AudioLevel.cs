namespace VoiceDictation.Core;

/// <summary>Loudness of microphone audio for the level meter shown while recording.</summary>
public static class AudioLevel
{
    private const double FloorDb = -60;
    private const double CeilingDb = -12;

    /// <summary>0 for silence (at or below -60 dBFS) up to 1 for loud speech (-12 dBFS and above), from the RMS.</summary>
    public static float Of(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        var rms = Math.Sqrt(sum / samples.Length);
        if (rms <= 0)
        {
            return 0;
        }

        var db = 20 * Math.Log10(rms);
        return (float)Math.Clamp((db - FloorDb) / (CeilingDb - FloorDb), 0, 1);
    }
}
