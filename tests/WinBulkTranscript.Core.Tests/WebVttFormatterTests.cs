using System.Text;
using WinBulkTranscript.Core.Domain;
using WinBulkTranscript.Core.Output;

namespace WinBulkTranscript.Core.Tests;

public sealed class WebVttFormatterTests
{
    [Fact]
    public void Format_EmptyCueList_ProducesHeaderOnlyDocument()
    {
        var result = WebVttFormatter.Format([]);

        Assert.Equal(0, result.CueCount);
        Assert.Equal("WEBVTT\n\n", result.Text);
    }

    [Fact]
    public void Format_SortsCues_SanitizesText_AndSkipsInvalidOrEmptyCues()
    {
        var result = WebVttFormatter.Format(
        [
            new TranscriptCue(new SpeechInterval(32_000, 48_000), "  second\r\n cue --> text  "),
            new TranscriptCue(new SpeechInterval(16_000, 32_000), "first\t cue"),
            new TranscriptCue(new SpeechInterval(0, 0), "invalid"),
            new TranscriptCue(new SpeechInterval(48_000, 64_000), " \r\n "),
        ]);

        Assert.Equal(2, result.CueCount);
        Assert.Equal(
            "WEBVTT\n\n" +
            "00:00:01.000 --> 00:00:02.000\n" +
            "first cue\n\n" +
            "00:00:02.000 --> 00:00:03.000\n" +
            "second cue → text\n\n",
            result.Text);
    }

    [Fact]
    public void Format_ClampsOverlappingRoundedCuesToStrictlyMonotonicTimestamps()
    {
        var result = WebVttFormatter.Format(
        [
            new TranscriptCue(new SpeechInterval(0, 16), "one"),
            new TranscriptCue(new SpeechInterval(8, 24), "two"),
        ]);

        Assert.Equal(
            "WEBVTT\n\n" +
            "00:00:00.000 --> 00:00:00.001\n" +
            "one\n\n" +
            "00:00:00.001 --> 00:00:00.002\n" +
            "two\n\n",
            result.Text);
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(7L, 0L)]
    [InlineData(8L, 1L)]
    [InlineData(16_000L, 1_000L)]
    [InlineData(1_600_008L, 100_001L)]
    public void RoundSamplesToMilliseconds_RoundsOnlyAtTheOutputBoundary(long samples, long expectedMilliseconds)
    {
        Assert.Equal(expectedMilliseconds, WebVttFormatter.RoundSamplesToMilliseconds(samples));
    }

    [Theory]
    [InlineData(0L, "00:00:00.000")]
    [InlineData(3_723_004L, "01:02:03.004")]
    [InlineData(97_200_999L, "27:00:00.999")]
    public void FormatTimestamp_AlwaysIncludesHours(long milliseconds, string expected)
    {
        Assert.Equal(expected, WebVttFormatter.FormatTimestamp(milliseconds));
    }

    [Fact]
    public void TimestampHelpers_RejectNegativeValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WebVttFormatter.RoundSamplesToMilliseconds(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebVttFormatter.FormatTimestamp(-1));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(" \t\r\n", "")]
    [InlineData("\t a\r\nb  \n", "a b")]
    [InlineData("a --> b", "a → b")]
    public void SanitizeText_NormalizesWhitespaceAndCueDelimiter(string? value, string expected)
    {
        Assert.Equal(expected, WebVttFormatter.SanitizeText(value));
    }
}

public sealed class WebVttWriterTests
{
    [Fact]
    public async Task WriteAsync_WritesUtf8WithoutBom_ReportsProgress_AndCleansTemporaryFile()
    {
        using var workspace = new TestWorkspace();
        var outputPath = Path.Combine(workspace.CreateDirectory("output"), "nested", "result.vtt");
        var progress = new InlineProgress<double>();
        var writer = new WebVttWriter();

        var result = await writer.WriteAsync(
            outputPath,
            [new TranscriptCue(new SpeechInterval(0, 16_000), "hello")],
            TranscriptCommitMode.FailIfExists,
            progress,
            CancellationToken.None);

        Assert.Equal(new TranscriptWriteResult(TranscriptWriteDisposition.Written, 1), result);
        var bytes = await File.ReadAllBytesAsync(outputPath);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal(
            "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nhello\n\n",
            Encoding.UTF8.GetString(bytes));
        Assert.Equal([0.10, 0.80, 1.00], progress.Values);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(outputPath)!, ".*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_FailIfExists_PreservesExistingFileWithoutWriting()
    {
        using var workspace = new TestWorkspace();
        var outputPath = workspace.CreateTextFile("existing.vtt", "existing content");
        var progress = new InlineProgress<double>();
        var writer = new WebVttWriter();

        var result = await writer.WriteAsync(
            outputPath,
            [new TranscriptCue(new SpeechInterval(0, 16_000), "new")],
            TranscriptCommitMode.FailIfExists,
            progress,
            CancellationToken.None);

        Assert.Equal(new TranscriptWriteResult(TranscriptWriteDisposition.SkippedExisting, 0), result);
        Assert.Equal("existing content", await File.ReadAllTextAsync(outputPath));
        Assert.Empty(progress.Values);
    }

    [Fact]
    public async Task WriteAsync_Overwrite_ReplacesExistingFileAndAllowsHeaderOnlyOutput()
    {
        using var workspace = new TestWorkspace();
        var outputPath = workspace.CreateTextFile("existing.vtt", "old");
        var writer = new WebVttWriter();

        var result = await writer.WriteAsync(
            outputPath,
            [],
            TranscriptCommitMode.Overwrite,
            progress: null,
            CancellationToken.None);

        Assert.Equal(new TranscriptWriteResult(TranscriptWriteDisposition.Written, 0), result);
        Assert.Equal("WEBVTT\n\n", await File.ReadAllTextAsync(outputPath));
    }

    [Fact]
    public async Task WriteAsync_PreCancelled_DoesNotCreateOrReplaceOutput()
    {
        using var workspace = new TestWorkspace();
        var outputPath = workspace.CreateTextFile("existing.vtt", "old");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var writer = new WebVttWriter();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(
            outputPath,
            [new TranscriptCue(new SpeechInterval(0, 16_000), "new")],
            TranscriptCommitMode.Overwrite,
            progress: null,
            cancellation.Token));

        Assert.Equal("old", await File.ReadAllTextAsync(outputPath));
    }

    [Fact]
    public async Task WriteAsync_CancelledBeforeCommit_PreservesExistingOutputAndDeletesTemporaryFile()
    {
        using var workspace = new TestWorkspace();
        var outputPath = workspace.CreateTextFile("existing.vtt", "old");
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress<double>(value =>
        {
            if (value >= 0.80d)
            {
                cancellation.Cancel();
            }
        });
        var writer = new WebVttWriter();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(
            outputPath,
            [new TranscriptCue(new SpeechInterval(0, 16_000), "new")],
            TranscriptCommitMode.Overwrite,
            progress,
            cancellation.Token));

        Assert.Equal("old", await File.ReadAllTextAsync(outputPath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(outputPath)!, ".*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_FailIfExists_LateCompetitorWinsWithoutLeavingTemporaryFile()
    {
        using var workspace = new TestWorkspace();
        var outputPath = Path.Combine(workspace.Root, "result.vtt");
        var wroteCompetitor = false;
        var progress = new CallbackProgress<double>(value =>
        {
            if (!wroteCompetitor && value >= 0.80d)
            {
                File.WriteAllText(outputPath, "competing final output");
                wroteCompetitor = true;
            }
        });
        var writer = new WebVttWriter();

        var result = await writer.WriteAsync(
            outputPath,
            [new TranscriptCue(new SpeechInterval(0, 16_000), "new")],
            TranscriptCommitMode.FailIfExists,
            progress,
            CancellationToken.None);

        Assert.True(wroteCompetitor);
        Assert.Equal(new TranscriptWriteResult(TranscriptWriteDisposition.SkippedExisting, 0), result);
        Assert.Equal("competing final output", await File.ReadAllTextAsync(outputPath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(outputPath)!, ".*.tmp"));
    }

    [Fact]
    public void CleanupStaleTemporaryFiles_RemovesOnlyRecognizedStaleWriterFiles()
    {
        using var workspace = new TestWorkspace();
        var outputRoot = workspace.CreateDirectory("output");
        var staleOwnedTemporary = workspace.CreateTextFile(
            Path.Combine("output", "nested", ".winbulktranscript-vtt.00112233445566778899aabbccddeeff.tmp"),
            "stale");
        var freshOwnedTemporary = workspace.CreateTextFile(
            Path.Combine("output", ".winbulktranscript-vtt.ffeeddccbbaa99887766554433221100.tmp"),
            "fresh");
        var genericVttLookalike = workspace.CreateTextFile(
            Path.Combine("output", ".clip.vtt.stale.tmp"),
            "preserve");
        var unrelatedTemporary = workspace.CreateTextFile(
            Path.Combine("output", ".unrelated.tmp"),
            "preserve");
        File.SetLastWriteTimeUtc(staleOwnedTemporary, DateTime.UtcNow - TimeSpan.FromDays(8));
        File.SetLastWriteTimeUtc(genericVttLookalike, DateTime.UtcNow - TimeSpan.FromDays(8));
        File.SetLastWriteTimeUtc(unrelatedTemporary, DateTime.UtcNow - TimeSpan.FromDays(8));

        WebVttWriter.CleanupStaleTemporaryFiles(outputRoot);

        Assert.False(File.Exists(staleOwnedTemporary));
        Assert.True(File.Exists(freshOwnedTemporary));
        Assert.True(File.Exists(genericVttLookalike));
        Assert.True(File.Exists(unrelatedTemporary));
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
