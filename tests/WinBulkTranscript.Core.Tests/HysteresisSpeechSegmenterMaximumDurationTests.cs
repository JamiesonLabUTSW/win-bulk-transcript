using WinBulkTranscript.Core.Audio;
using WinBulkTranscript.Core.Domain;

namespace WinBulkTranscript.Core.Tests;

public sealed class HysteresisSpeechSegmenterMaximumDurationTests
{
    [Fact]
    public void Complete_NaturalClosureNeverExceedsConfiguredMaximumSegmentLength()
    {
        var segmenter = new HysteresisSpeechSegmenter(new HysteresisSegmenterOptions
        {
            OnsetFrames = 1,
            SilenceFrames = 1,
            PreRollSamples = 10,
            PostRollSamples = 20,
            MinimumSpeechSamples = 1,
            MergeGapSamples = 0,
            MaximumSegmentSamples = 30,
            SplitSearchSamples = 10,
        });

        segmenter.ProcessFrame(new VadFrame(0, 10, -90, false, true));
        segmenter.ProcessFrame(new VadFrame(10, 20, -10, true, false));
        segmenter.ProcessFrame(new VadFrame(20, 30, -10, true, false));
        segmenter.ProcessFrame(new VadFrame(30, 40, -90, false, true));

        var intervals = segmenter.Complete(40);

        Assert.Equal([new SpeechInterval(0, 30), new SpeechInterval(30, 40)], intervals);
        Assert.All(intervals, interval => Assert.InRange(interval.LengthSamples, 1, 30));
    }
}
