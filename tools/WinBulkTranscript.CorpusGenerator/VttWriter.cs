using System.Globalization;
using System.Text;

namespace WinBulkTranscript.CorpusGenerator;

/// <summary>
/// Renders expected WebVTT files directly from master sample boundaries.
/// </summary>
internal static class VttWriter
{
    /// <summary>
    /// Renders all fixture utterances as authored-text WebVTT cues.
    /// </summary>
    /// <param name="fixture">The fixture ground truth.</param>
    /// <returns>UTF-8 text content using LF newlines.</returns>
    public static string Render(FixtureManifest fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var builder = new StringBuilder("WEBVTT\n\n");
        foreach (var utterance in fixture.Utterances)
        {
            builder.Append(FormatTimestamp(utterance.StartSample));
            builder.Append(" --> ");
            builder.Append(FormatTimestamp(utterance.EndSample));
            builder.Append('\n');
            builder.Append(utterance.AuthoredText);
            builder.Append("\n\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formats a master sample index as a WebVTT timestamp, rounded to the nearest millisecond.
    /// </summary>
    /// <param name="sampleIndex">The nonnegative PCM sample index.</param>
    /// <returns>An HH:MM:SS.mmm timestamp.</returns>
    public static string FormatTimestamp(long sampleIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleIndex);

        var milliseconds = checked((sampleIndex * 1_000L + (Pcm16Audio.SampleRate / 2L)) / Pcm16Audio.SampleRate);
        var hours = milliseconds / 3_600_000L;
        milliseconds %= 3_600_000L;
        var minutes = milliseconds / 60_000L;
        milliseconds %= 60_000L;
        var seconds = milliseconds / 1_000L;
        milliseconds %= 1_000L;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours:D2}:{minutes:D2}:{seconds:D2}.{milliseconds:D3}");
    }
}
