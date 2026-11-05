namespace VoiceDictation.Core;

public static class SpeechSegments
{
    /// <summary>
    /// Splits audio into chunks of at most <paramref name="maxMs"/> for transcription, cutting only in pauses
    /// between detected speech regions where possible (long push-to-talk dictation exceeds Whisper's window).
    /// Returns no chunks when there is no speech.
    /// </summary>
    public static IReadOnlyList<float[]> Chunk(
        float[] samples,
        IReadOnlyList<(TimeSpan Start, TimeSpan End)> speech,
        int sampleRate,
        int maxMs,
        int padMs)
    {
        var max = sampleRate * maxMs / 1000;
        var pad = sampleRate * padMs / 1000;
        var chunks = new List<float[]>();
        int? chunkStart = null;
        var chunkEnd = 0;
        foreach (var (startTime, endTime) in speech.OrderBy(s => s.Start))
        {
            var start = Math.Clamp((int)(startTime.TotalSeconds * sampleRate) - pad, 0, samples.Length);
            var end = Math.Clamp((int)(endTime.TotalSeconds * sampleRate) + pad, start, samples.Length);
            if (end <= start)
            {
                continue;
            }

            if (chunkStart is { } s && end - s > max)
            {
                chunks.Add(samples[s..chunkEnd]);
                chunkStart = null;
            }

            chunkStart ??= start;
            chunkEnd = Math.Max(chunkEnd, end);

            // A single region longer than the window: cut it into window-sized pieces.
            while (chunkEnd - chunkStart.Value > max)
            {
                chunks.Add(samples[chunkStart.Value..(chunkStart.Value + max)]);
                chunkStart += max;
            }
        }

        if (chunkStart is { } last && chunkEnd > last)
        {
            chunks.Add(samples[last..chunkEnd]);
        }

        return chunks;
    }

    /// <summary>
    /// Splits an utterance at pauses of at least <paramref name="minGapMs"/> between detected speech regions,
    /// so a command that background noise glued to the previous sentence becomes its own utterance again.
    /// Returns no pieces when there is no speech at all.
    /// </summary>
    public static IReadOnlyList<float[]> Split(
        float[] samples,
        IReadOnlyList<(TimeSpan Start, TimeSpan End)> speech,
        int sampleRate,
        int minGapMs,
        int padMs)
    {
        var minGap = sampleRate * minGapMs / 1000;
        var pad = sampleRate * padMs / 1000;
        var merged = new List<(int Start, int End)>();
        foreach (var (startTime, endTime) in speech.OrderBy(s => s.Start))
        {
            var start = Math.Clamp((int)(startTime.TotalSeconds * sampleRate), 0, samples.Length);
            var end = Math.Clamp((int)(endTime.TotalSeconds * sampleRate), start, samples.Length);
            if (end <= start)
            {
                continue;
            }

            if (merged.Count > 0 && start - merged[^1].End < minGap)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            }
            else
            {
                merged.Add((start, end));
            }
        }

        return merged
            .Select(m =>
            {
                var from = Math.Max(0, m.Start - pad);
                var to = Math.Min(samples.Length, m.End + pad);
                return samples[from..to];
            })
            .ToList();
    }
}
