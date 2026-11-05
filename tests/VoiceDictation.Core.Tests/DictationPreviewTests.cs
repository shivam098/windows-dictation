using VoiceDictation.Core;

namespace VoiceDictation.Core.Tests;

public class DictationPreviewTests
{
    private const int Rate = 16000;

    private static (TimeSpan, TimeSpan) Seg(double start, double end) => (TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));

    /// <summary>Audio whose sample values are their absolute index, so fakes can tell which part they got.</summary>
    private static float[] Audio(double seconds) => Enumerable.Range(0, (int)(Rate * seconds)).Select(i => (float)i).ToArray();

    /// <summary>Speech detector over absolute regions, answering for any slice of <see cref="Audio"/>.</summary>
    private static Func<float[], IReadOnlyList<(TimeSpan Start, TimeSpan End)>> Detector(params (TimeSpan Start, TimeSpan End)[] absolute) =>
        slice =>
        {
            var from = slice.Length == 0 ? 0 : slice[0] / Rate;
            var to = from + ((double)slice.Length / Rate);
            return absolute
                .Where(r => r.End.TotalSeconds > from && r.Start.TotalSeconds < to)
                .Select(r => Seg(Math.Max(r.Start.TotalSeconds, from) - from, Math.Min(r.End.TotalSeconds, to) - from))
                .ToList();
        };

    private static string Label(float[] slice) => $"[{slice[0] / Rate:0.#}-{(slice[^1] + 1) / Rate:0.#}]";

    [Fact]
    public async Task Short_recording_is_transcribed_whole_each_update()
    {
        var calls = new List<string>();
        var preview = new DictationPreview(a => { calls.Add(Label(a)); return Task.FromResult("hello"); }, Detector(Seg(0.5, 3)), Rate);

        Assert.Equal("hello", await preview.UpdateAsync(Audio(2)));
        Assert.Equal("hello", await preview.UpdateAsync(Audio(4)));
        Assert.Equal(["[0-2]", "[0-4]"], calls);
        Assert.Equal(0, preview.CommittedSamples);
    }

    [Fact]
    public async Task Silence_is_not_transcribed()
    {
        var calls = 0;
        var preview = new DictationPreview(_ => { calls++; return Task.FromResult("you"); }, Detector(), Rate);

        Assert.Equal(string.Empty, await preview.UpdateAsync(Audio(3)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Long_recording_commits_the_oldest_part_at_a_pause_and_keeps_its_text()
    {
        var calls = new List<string>();
        var preview = new DictationPreview(
            a => { calls.Add(Label(a)); return Task.FromResult(Label(a)); },
            Detector(Seg(0, 5), Seg(6, 11), Seg(12.5, 20)),
            Rate);

        var text = await preview.UpdateAsync(Audio(16));

        // Latest pause inside the 12 s window is between 11 s and 12.5 s.
        Assert.Equal(Rate * 23 / 2 + Rate * 1 / 4, preview.CommittedSamples);
        Assert.Equal(["[0-11.8]", "[11.8-16]"], calls);
        Assert.Equal("[0-11.8] [11.8-16]", text);

        calls.Clear();
        var audio = Audio(18);
        text = await preview.UpdateAsync(audio[preview.CommittedSamples..]);
        Assert.Equal(["[11.8-18]"], calls);
        Assert.Equal("[0-11.8] [11.8-18]", text);
    }

    [Fact]
    public async Task Unbroken_speech_is_cut_at_the_window()
    {
        var preview = new DictationPreview(a => Task.FromResult(Label(a)), Detector(Seg(0, 30)), Rate);

        var text = await preview.UpdateAsync(Audio(30));

        Assert.Equal(Rate * 24, preview.CommittedSamples);
        Assert.Equal("[0-12] [12-24] [24-30]", text);
    }

    [Fact]
    public async Task Long_silence_is_dropped_without_transcribing()
    {
        var calls = 0;
        var preview = new DictationPreview(_ => { calls++; return Task.FromResult("x"); }, Detector(), Rate);

        Assert.Equal(string.Empty, await preview.UpdateAsync(Audio(20)));
        Assert.Equal(Rate * 19, preview.CommittedSamples);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Without_a_detector_all_audio_counts_as_speech()
    {
        var preview = new DictationPreview(a => Task.FromResult(Label(a)), null, Rate);

        Assert.Equal("[0-2]", await preview.UpdateAsync(Audio(2)));
    }

    [Fact]
    public void Commit_point_prefers_the_latest_pause_inside_the_window()
    {
        var cut = DictationPreview.FindCommitPoint([Seg(0, 2), Seg(3, 6), Seg(7, 13)], Rate * 13, Rate, Rate * 12, Rate * 3);
        Assert.Equal(Rate * 13 / 2, cut);
    }

    [Fact]
    public void Commit_point_ignores_pauses_too_early_to_be_worth_committing()
    {
        var cut = DictationPreview.FindCommitPoint([Seg(0, 1), Seg(1.5, 14)], Rate * 14, Rate, Rate * 12, Rate * 3);
        Assert.Equal(Rate * 12, cut);
    }
}
