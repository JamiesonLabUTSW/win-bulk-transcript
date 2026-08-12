using WinBulkTranscript.Core.Transcription;

namespace WinBulkTranscript.Core.Tests;

public sealed class StreamingTranscriptAccumulatorTests
{
    [Fact]
    public void Append_DeltaChunks_PreservesEmissionOrder()
    {
        var accumulator = new StreamingTranscriptAccumulator();

        accumulator.Append(" This");
        accumulator.Append(" is");
        accumulator.Append(" text.");

        Assert.Equal(" This is text.", accumulator.Text);
    }

    [Fact]
    public void Append_FinalAggregateEqualToAccumulatedText_DoesNotDuplicateTranscript()
    {
        var accumulator = new StreamingTranscriptAccumulator();

        accumulator.Append(" This");
        accumulator.Append(" is");
        accumulator.Append(" text.");
        accumulator.Append(" This is text.");

        Assert.Equal(" This is text.", accumulator.Text);
    }

    [Fact]
    public void Append_LongerCumulativeSnapshot_AppendsOnlyItsNewSuffix()
    {
        var accumulator = new StreamingTranscriptAccumulator();

        accumulator.Append(" This");
        accumulator.Append(" This is text.");

        Assert.Equal(" This is text.", accumulator.Text);
    }

    [Fact]
    public void Append_ShorterCumulativeSnapshot_DoesNotDiscardOrDuplicateText()
    {
        var accumulator = new StreamingTranscriptAccumulator();

        accumulator.Append(" This is text.");
        accumulator.Append(" This");

        Assert.Equal(" This is text.", accumulator.Text);
    }

    [Fact]
    public void Append_NonPrefixText_AppendsVerbatimWithoutOverlapGuessing()
    {
        var accumulator = new StreamingTranscriptAccumulator();

        accumulator.Append("repeat");
        accumulator.Append("eat");

        Assert.Equal("repeateat", accumulator.Text);
    }

    [Fact]
    public void Append_EmptyText_IsIgnored()
    {
        var accumulator = new StreamingTranscriptAccumulator();

        accumulator.Append(string.Empty);

        Assert.Equal(string.Empty, accumulator.Text);
    }
}
