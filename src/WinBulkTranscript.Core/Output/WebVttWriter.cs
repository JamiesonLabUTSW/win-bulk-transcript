using System.Text;
using WinBulkTranscript.Core.Domain;
using WinBulkTranscript.Core.Ports;

namespace WinBulkTranscript.Core.Output;

/// <summary>Writes UTF-8 WebVTT safely through a same-directory temporary file.</summary>
public sealed class WebVttWriter : ITranscriptWriter
{
    private const string TemporaryFilePrefix = ".winbulktranscript-vtt.";
    private const string TemporaryFileSuffix = ".tmp";
    private static readonly TimeSpan StaleTemporaryFileAge = TimeSpan.FromDays(7);
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Removes only aged, application-owned VTT commit temporary files below an output root.
    /// This is best effort: it never follows reparse points and never changes final VTT files.
    /// </summary>
    public static void CleanupStaleTemporaryFiles(string outputRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

        var root = Path.GetFullPath(outputRoot);
        if (!Directory.Exists(root))
        {
            return;
        }

        var threshold = DateTime.UtcNow - StaleTemporaryFileAge;
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                root,
                "*.tmp",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                }))
            {
                try
                {
                    var file = new FileInfo(path);
                    if (IsOwnedTemporaryFileName(file.Name) && file.LastWriteTimeUtc <= threshold)
                    {
                        file.Delete();
                    }
                }
                catch (Exception) when (File.Exists(path))
                {
                    // A locked stale temporary file can be retried at the next batch start.
                }
            }
        }
        catch (Exception) when (Directory.Exists(root))
        {
            // Cleanup must not prevent the preflight diagnostics for a new batch.
        }
    }

    public async Task<TranscriptWriteResult> WriteAsync(
        string outputPath,
        IReadOnlyList<TranscriptCue> cues,
        TranscriptCommitMode commitMode,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(cues);
        cancellationToken.ThrowIfCancellationRequested();

        if (commitMode == TranscriptCommitMode.FailIfExists && File.Exists(outputPath))
        {
            return new TranscriptWriteResult(TranscriptWriteDisposition.SkippedExisting, 0);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("A VTT output path must have a containing directory.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, CreateTemporaryFileName());
        var document = WebVttFormatter.Format(cues);
        progress?.Report(0.10);

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, Utf8WithoutBom, 64 * 1024, leaveOpen: true))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteAsync(document.Text.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            progress?.Report(0.80);
            cancellationToken.ThrowIfCancellationRequested();

            if (commitMode == TranscriptCommitMode.Overwrite)
            {
                File.Move(temporaryPath, outputPath, overwrite: true);
            }
            else
            {
                try
                {
                    File.Move(temporaryPath, outputPath);
                }
                catch (IOException) when (File.Exists(outputPath))
                {
                    return new TranscriptWriteResult(TranscriptWriteDisposition.SkippedExisting, 0);
                }
            }

            progress?.Report(1);
            return new TranscriptWriteResult(TranscriptWriteDisposition.Written, document.CueCount);
        }
        finally
        {
            await TryDeleteTemporaryFileAsync(temporaryPath).ConfigureAwait(false);
        }
    }

    private static bool IsOwnedTemporaryFileName(string fileName)
    {
        if (!fileName.StartsWith(TemporaryFilePrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(TemporaryFileSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var identifierLength = fileName.Length - TemporaryFilePrefix.Length - TemporaryFileSuffix.Length;
        return identifierLength == 32
            && Guid.TryParseExact(fileName.AsSpan(TemporaryFilePrefix.Length, identifierLength), "N", out _);
    }

    private static string CreateTemporaryFileName()
        => string.Concat(TemporaryFilePrefix, Guid.NewGuid().ToString("N"), TemporaryFileSuffix);

    private static async ValueTask TryDeleteTemporaryFileAsync(string temporaryPath)
    {
        const int maximumAttempts = 3;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            try
            {
                if (!File.Exists(temporaryPath))
                {
                    return;
                }

                File.Delete(temporaryPath);
                return;
            }
            catch (IOException) when (attempt < maximumAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1))).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < maximumAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1))).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A failed cleanup is intentionally non-fatal: it cannot publish a partial final VTT.
                return;
            }
            catch (UnauthorizedAccessException)
            {
                // A later batch can retry a stale owned temporary file without touching final VTTs.
                return;
            }
        }
    }
}

/// <summary>Pure WebVTT formatting and timestamp rules, independently testable from file I/O.</summary>
public static class WebVttFormatter
{
    public const int SamplesPerSecond = 16_000;

    public static FormattedVtt Format(IReadOnlyList<TranscriptCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);

        var builder = new StringBuilder("WEBVTT\n\n");
        var cueCount = 0;
        var previousEndMilliseconds = 0L;

        foreach (var cue in cues.OrderBy(static cue => cue.Interval.StartSample).ThenBy(static cue => cue.Interval.EndSample))
        {
            if (!cue.Interval.IsValid)
            {
                continue;
            }

            var text = SanitizeText(cue.Text);
            if (text.Length == 0)
            {
                continue;
            }

            var startMilliseconds = Math.Max(previousEndMilliseconds, RoundSamplesToMilliseconds(cue.Interval.StartSample));
            var endMilliseconds = Math.Max(startMilliseconds + 1, RoundSamplesToMilliseconds(cue.Interval.EndSample));
            previousEndMilliseconds = endMilliseconds;

            builder.Append(FormatTimestamp(startMilliseconds));
            builder.Append(" --> ");
            builder.Append(FormatTimestamp(endMilliseconds));
            builder.Append('\n');
            builder.Append(text);
            builder.Append("\n\n");
            cueCount++;
        }

        return new FormattedVtt(builder.ToString(), cueCount);
    }

    public static long RoundSamplesToMilliseconds(long sampleIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleIndex);

        // Rounded once at the output boundary. The values used by this application remain far below
        // the checked multiplication limit, and checked arithmetic prevents a silent bad timestamp.
        return checked((sampleIndex * 1_000L + (SamplesPerSecond / 2)) / SamplesPerSecond);
    }

    public static string FormatTimestamp(long milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);

        var hours = milliseconds / 3_600_000;
        var minutes = (milliseconds / 60_000) % 60;
        var seconds = (milliseconds / 1_000) % 60;
        var fraction = milliseconds % 1_000;
        return $"{hours:00}:{minutes:00}:{seconds:00}.{fraction:000}";
    }

    public static string SanitizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Normalize(NormalizationForm.FormC).Replace("-->", "→", StringComparison.Ordinal);
        var builder = new StringBuilder(normalized.Length);
        var previousWasWhitespace = false;
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                previousWasWhitespace = true;
            }
            else
            {
                builder.Append(character);
                previousWasWhitespace = false;
            }
        }

        return builder.ToString().TrimEnd();
    }
}

public sealed record FormattedVtt(string Text, int CueCount);
