using System.Text;

namespace WinBulkTranscript.Core.Transcription;

/// <summary>
/// Accumulates response text emitted by a streaming recognizer without duplicating exact
/// cumulative snapshots.
/// </summary>
/// <remarks>
/// Some recognizers emit ordinary delta chunks and then repeat the complete transcript as a
/// final response. This class only recognizes ordinal-exact relationships to the complete text
/// accumulated so far: an equal or shorter prefix is ignored, and a longer cumulative prefix
/// contributes only its new suffix. All other text is appended verbatim. In particular, it does
/// not normalize whitespace, infer token boundaries, or remove partial overlaps, because those
/// guesses could remove legitimate recognized speech.
/// </remarks>
public sealed class StreamingTranscriptAccumulator
{
    private readonly StringBuilder _text = new();

    /// <summary>Gets the accumulated response text.</summary>
    public string Text => _text.ToString();

    /// <summary>
    /// Adds one nonempty response-text emission while preserving its order.
    /// </summary>
    /// <param name="text">The text emitted by the recognizer.</param>
    public void Append(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return;
        }

        if (_text.Length == 0)
        {
            _text.Append(text);
            return;
        }

        var current = _text.ToString();
        if (text.AsSpan().SequenceEqual(current.AsSpan()))
        {
            return;
        }

        if (text.AsSpan().StartsWith(current.AsSpan(), StringComparison.Ordinal))
        {
            _text.Append(text.AsSpan(current.Length));
            return;
        }

        if (current.AsSpan().StartsWith(text.AsSpan(), StringComparison.Ordinal))
        {
            return;
        }

        _text.Append(text);
    }
}
