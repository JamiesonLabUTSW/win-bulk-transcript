using WinBulkTranscript.Core.Audio;
using WinBulkTranscript.Core.Domain;

namespace WinBulkTranscript.Core.Tests;

public sealed class HysteresisSpeechSegmenterTests
{
    [Fact]
    public void ProcessFrame_RequiresContiguousPositiveFrames()
    {
        var segmenter = new HysteresisSpeechSegmenter();

        segmenter.ProcessFrame(Frame(0, 10, isAboveOnThreshold: false, isBelowOffThreshold: true));

        Assert.Throws<ArgumentException>(() => segmenter.ProcessFrame(Frame(11, 20, false, true)));
        Assert.Throws<ArgumentException>(() => new HysteresisSpeechSegmenter().ProcessFrame(Frame(0, 0, false, true)));
    }

    [Fact]
    public void Complete_RequiresExactProcessedSampleCount_AndIsIdempotentForTheSameCount()
    {
        var segmenter = new HysteresisSpeechSegmenter();
        segmenter.ProcessFrame(Frame(0, 10, false, true));

        Assert.Throws<ArgumentException>(() => segmenter.Complete(11));

        var first = segmenter.Complete(10);
        var second = segmenter.Complete(10);

        Assert.Same(first, second);
        Assert.Empty(first);
        Assert.Throws<InvalidOperationException>(() => segmenter.Complete(9));
        Assert.Throws<InvalidOperationException>(() => segmenter.ProcessFrame(Frame(10, 20, false, true)));
    }

    [Fact]
    public void Segmenter_ConfirmsOnset_UsesPadding_AndClosesAfterSustainedSilence()
    {
        var segmenter = new HysteresisSpeechSegmenter(new HysteresisSegmenterOptions
        {
            OnsetFrames = 2,
            SilenceFrames = 2,
            PreRollSamples = 5,
            PostRollSamples = 5,
            MinimumSpeechSamples = 1,
            MergeGapSamples = 0,
            MaximumSegmentSamples = 100,
            SplitSearchSamples = 10,
        });

        segmenter.ProcessFrame(Frame(0, 10, false, true));
        segmenter.ProcessFrame(Frame(10, 20, true, false));
        segmenter.ProcessFrame(Frame(20, 30, true, false));
        segmenter.ProcessFrame(Frame(30, 40, false, false));
        segmenter.ProcessFrame(Frame(40, 50, false, true));
        segmenter.ProcessFrame(Frame(50, 60, false, true));

        var intervals = segmenter.Complete(60);

        Assert.Equal([new SpeechInterval(5, 45)], intervals);
        Assert.False(segmenter.IsInSpeech);
    }

    [Fact]
    public void Segmenter_DropsNaturallyClosedSpeechShorterThanMinimumDuration()
    {
        var segmenter = new HysteresisSpeechSegmenter(new HysteresisSegmenterOptions
        {
            OnsetFrames = 1,
            SilenceFrames = 1,
            PreRollSamples = 0,
            PostRollSamples = 0,
            MinimumSpeechSamples = 11,
            MergeGapSamples = 0,
            MaximumSegmentSamples = 100,
            SplitSearchSamples = 10,
        });

        segmenter.ProcessFrame(Frame(0, 10, true, false));
        segmenter.ProcessFrame(Frame(10, 20, false, true));

        Assert.Empty(segmenter.Complete(20));
    }

    [Fact]
    public void Segmenter_FlushesOpenSpeechAtEndOfFile()
    {
        var segmenter = new HysteresisSpeechSegmenter(new HysteresisSegmenterOptions
        {
            OnsetFrames = 1,
            SilenceFrames = 2,
            PreRollSamples = 0,
            PostRollSamples = 5,
            MinimumSpeechSamples = 1,
            MergeGapSamples = 0,
            MaximumSegmentSamples = 100,
            SplitSearchSamples = 10,
        });

        segmenter.ProcessFrame(Frame(0, 10, true, false));
        segmenter.ProcessFrame(Frame(10, 20, true, false));

        Assert.Equal([new SpeechInterval(0, 20)], segmenter.Complete(20));
    }

    [Fact]
    public void Segmenter_MergesNearbyNaturallyClosedIntervalsWithoutExceedingMaximumLength()
    {
        var segmenter = new HysteresisSpeechSegmenter(new HysteresisSegmenterOptions
        {
            OnsetFrames = 1,
            SilenceFrames = 1,
            PreRollSamples = 0,
            PostRollSamples = 0,
            MinimumSpeechSamples = 1,
            MergeGapSamples = 10,
            MaximumSegmentSamples = 100,
            SplitSearchSamples = 10,
        });

        segmenter.ProcessFrame(Frame(0, 10, true, false));
        segmenter.ProcessFrame(Frame(10, 20, false, true));
        segmenter.ProcessFrame(Frame(20, 30, true, false));
        segmenter.ProcessFrame(Frame(30, 40, false, true));

        Assert.Equal([new SpeechInterval(0, 30)], segmenter.Complete(40));
    }

    [Fact]
    public void Segmenter_ForcesMaximumLengthSplitAtRecentLowestEnergyBoundary()
    {
        var segmenter = new HysteresisSpeechSegmenter(new HysteresisSegmenterOptions
        {
            OnsetFrames = 1,
            SilenceFrames = 10,
            PreRollSamples = 0,
            PostRollSamples = 0,
            MinimumSpeechSamples = 1,
            MergeGapSamples = 0,
            MaximumSegmentSamples = 30,
            SplitSearchSamples = 20,
            SplitOverlapSamples = 0,
        });

        segmenter.ProcessFrame(Frame(0, 10, true, false, -10));
        segmenter.ProcessFrame(Frame(10, 20, true, false, -20));
        segmenter.ProcessFrame(Frame(20, 30, true, false, -80));
        segmenter.ProcessFrame(Frame(30, 40, true, false, -15));

        var intervals = segmenter.Complete(40);

        Assert.Equal([new SpeechInterval(0, 20), new SpeechInterval(20, 40)], intervals);
        Assert.All(intervals, interval => Assert.InRange(interval.LengthSamples, 1, 30));
    }

    [Theory]
    [InlineData(0, 1, 0, 0, 1, 1, 1, 1, 1)]
    [InlineData(1, 0, 0, 0, 1, 1, 1, 1, 1)]
    [InlineData(1, 1, -1, 0, 1, 1, 1, 1, 1)]
    [InlineData(1, 1, 0, -1, 1, 1, 1, 1, 1)]
    [InlineData(1, 1, 0, 0, 1, 1, 1, 0, 1)]
    public void Constructor_RejectsInvalidTimingConfiguration(
        int onsetFrames,
        int silenceFrames,
        long preRollSamples,
        long postRollSamples,
        long minimumSpeechSamples,
        long mergeGapSamples,
        long maximumSegmentSamples,
        long splitSearchSamples,
        long splitOverlapSamples)
    {
        var options = new HysteresisSegmenterOptions
        {
            OnsetFrames = onsetFrames,
            SilenceFrames = silenceFrames,
            PreRollSamples = preRollSamples,
            PostRollSamples = postRollSamples,
            MinimumSpeechSamples = minimumSpeechSamples,
            MergeGapSamples = mergeGapSamples,
            MaximumSegmentSamples = maximumSegmentSamples,
            SplitSearchSamples = splitSearchSamples,
            SplitOverlapSamples = splitOverlapSamples,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new HysteresisSpeechSegmenter(options));
    }

    private static VadFrame Frame(
        long startSample,
        long endSample,
        bool isAboveOnThreshold,
        bool isBelowOffThreshold,
        double decibelsFullScale = -30)
        => new(startSample, endSample, decibelsFullScale, isAboveOnThreshold, isBelowOffThreshold);
}
