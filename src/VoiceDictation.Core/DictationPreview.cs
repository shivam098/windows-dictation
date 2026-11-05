namespace VoiceDictation.Core;

/// <summary>
/// Live transcript while push-to-talk or hands-free dictation is recording. Each update re-transcribes the
/// audio after the last committed point; once that exceeds the recognizer window, the oldest part is
/// transcribed once, cut at a pause, and kept, so each update costs at most a window of audio.
/// </summary>
public sealed class DictationPreview
{
    private readonly Func<float[], Task<string>> _transcribe;
    private readonly Func<float[], IReadOnlyList<(TimeSpan Start, TimeSpan End)>>? _detect;
    private readonly int _sampleRate;
    private readonly int _windowSamples;
    private readonly int _minCommitSamples;
    private readonly List<string> _committed = [];

    /// <param name="transcribe">Returns cleaned text for the audio (empty when nothing was said).</param>
    /// <param name="detect">Speech regions in the audio; null treats all audio as speech.</param>
    public DictationPreview(
        Func<float[], Task<string>> transcribe,
        Func<float[], IReadOnlyList<(TimeSpan Start, TimeSpan End)>>? detect,
        int sampleRate,
        int windowMs = 12000,
        int minCommitMs = 3000)
    {
        _transcribe = transcribe;
        _detect = detect;
        _sampleRate = sampleRate;
        _windowSamples = sampleRate * windowMs / 1000;
        _minCommitSamples = Math.Min(sampleRate * minCommitMs / 1000, _windowSamples);
    }

    /// <summary>Samples from the start of the recording that are already transcribed and kept.</summary>
    public int CommittedSamples { get; private set; }

    /// <summary>Transcribes <paramref name="pending"/> (the audio recorded after <see cref="CommittedSamples"/>).</summary>
    public async Task<string> UpdateAsync(float[] pending)
    {
        var speech = Detect(pending);
        while (pending.Length > _windowSamples)
        {
            var cut = FindCommitPoint(speech, pending.Length, _sampleRate, _windowSamples, _minCommitSamples);
            if (speech.Any(s => (int)(s.Start.TotalSeconds * _sampleRate) < cut))
            {
                var text = await _transcribe(pending[..cut]).ConfigureAwait(false);
                if (text.Length > 0)
                {
                    _committed.Add(text);
                }
            }

            CommittedSamples += cut;
            pending = pending[cut..];
            speech = Detect(pending);
        }

        var tentative = speech.Count == 0 ? string.Empty : await _transcribe(pending).ConfigureAwait(false);
        return string.Join(" ", _committed.Append(tentative).Where(t => t.Length > 0));
    }

    /// <summary>
    /// Where to cut audio longer than the window: in the latest pause between <paramref name="minSamples"/> and
    /// <paramref name="windowSamples"/>, else the window edge. Audio with no speech is dropped except its last second.
    /// </summary>
    public static int FindCommitPoint(
        IReadOnlyList<(TimeSpan Start, TimeSpan End)> speech,
        int length,
        int sampleRate,
        int windowSamples,
        int minSamples)
    {
        if (speech.Count == 0)
        {
            return Math.Max(1, length - sampleRate);
        }

        var regions = speech
            .Select(s => (Start: (int)(s.Start.TotalSeconds * sampleRate), End: (int)(s.End.TotalSeconds * sampleRate)))
            .OrderBy(s => s.Start)
            .ToList();
        var candidates = regions.Zip(regions.Skip(1), (a, b) => (a.End + b.Start) / 2).ToList();
        if (regions[^1].End < length)
        {
            candidates.Add(Math.Min(length, regions[^1].End + (sampleRate / 5)));
        }

        var best = candidates.Where(c => c >= minSamples && c <= windowSamples).DefaultIfEmpty(0).Max();
        return best > 0 ? best : windowSamples;
    }

    private IReadOnlyList<(TimeSpan Start, TimeSpan End)> Detect(float[] audio) =>
        _detect is null ? [(TimeSpan.Zero, TimeSpan.FromSeconds((double)audio.Length / _sampleRate))] : _detect(audio);
}
