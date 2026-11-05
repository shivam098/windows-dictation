using VoiceDictation.Core;

namespace VoiceDictation.Core.Tests;

public class SpeechSegmentsTests
{
    private const int Rate = 16000;
    private static readonly float[] TenSeconds = Enumerable.Range(0, Rate * 10).Select(i => (float)i).ToArray();

    private static (TimeSpan, TimeSpan) Seg(double start, double end) => (TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));

    [Fact]
    public void No_speech_gives_no_pieces()
    {
        Assert.Empty(SpeechSegments.Split(TenSeconds, [], Rate, 600, 250));
        Assert.Empty(SpeechSegments.Chunk(TenSeconds, [], Rate, 14000, 200));
    }

    [Fact]
    public void Split_separates_regions_with_a_long_gap_and_merges_short_gaps()
    {
        var pieces = SpeechSegments.Split(TenSeconds, [Seg(1, 3), Seg(3.3, 4), Seg(6, 7)], Rate, 600, 250);
        Assert.Equal(2, pieces.Count);
        Assert.Equal(Rate * 0.75, pieces[0][0]);
        Assert.Equal((int)(Rate * 4.25), pieces[0].Length + (int)pieces[0][0]);
        Assert.Equal(Rate * 5.75, pieces[1][0]);
    }

    [Fact]
    public void Chunk_keeps_short_dictation_whole()
    {
        var chunks = SpeechSegments.Chunk(TenSeconds, [Seg(1, 3), Seg(4, 6)], Rate, 14000, 200);
        Assert.Single(chunks);
    }

    [Fact]
    public void Chunk_cuts_long_dictation_at_pauses_within_the_limit()
    {
        var audio = new float[Rate * 40];
        var chunks = SpeechSegments.Chunk(audio, [Seg(0, 8), Seg(9, 13), Seg(14, 20), Seg(21, 30)], Rate, 14000, 200);
        Assert.All(chunks, c => Assert.True(c.Length <= Rate * 14));
        Assert.Equal(3, chunks.Count);
    }

    [Fact]
    public void Chunk_hard_splits_a_single_overlong_region()
    {
        var audio = new float[Rate * 40];
        var chunks = SpeechSegments.Chunk(audio, [Seg(0, 30)], Rate, 14000, 0);
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Length <= Rate * 14));
        Assert.Equal(Rate * 30, chunks.Sum(c => c.Length));
    }
}
