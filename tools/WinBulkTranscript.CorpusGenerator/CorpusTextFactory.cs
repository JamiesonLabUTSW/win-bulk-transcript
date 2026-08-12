using System.Globalization;
using System.Text;

namespace WinBulkTranscript.CorpusGenerator;

/// <summary>
/// Produces controlled, ordinary English utterances from checked-in vocabulary and sentence templates.
/// </summary>
internal sealed class CorpusTextFactory
{
    private static readonly string[] Adjectives =
    [
        "calm", "clear", "careful", "bright", "gentle", "steady", "quiet", "simple", "useful", "patient",
        "warm", "fresh", "kind", "small", "ready", "plain", "thoughtful", "helpful", "balanced", "pleasant",
    ];

    private static readonly string[] Nouns =
    [
        "teacher", "reader", "worker", "gardener", "traveler", "artist", "neighbor", "friend", "student", "keeper",
        "writer", "parent", "visitor", "sailor", "builder", "driver", "singer", "helper", "leader", "walker",
    ];

    private static readonly string[] Objects =
    [
        "report", "message", "window", "garden", "map", "notebook", "path", "table", "letter", "basket",
        "picture", "lantern", "answer", "plan", "bridge", "book", "trail", "parcel", "blanket", "story",
    ];

    private static readonly string[] Places =
    [
        "harbor", "station", "kitchen", "library", "meadow", "market", "workshop", "village", "porch", "park",
        "valley", "hall", "garden", "corner", "shore", "room", "path", "square", "field", "office",
    ];

    private static readonly string[] Verbs =
    [
        "checks", "carries", "places", "finds", "shares", "keeps", "moves", "reads", "writes", "brings",
        "follows", "opens", "watches", "uses", "guides", "builds", "offers", "chooses", "holds", "starts",
    ];

    private static readonly string[] Adverbs =
    [
        "carefully", "quietly", "slowly", "kindly", "clearly", "patiently", "gently", "readily", "warmly", "steadily",
    ];

    private static readonly string[] TimeWords =
    [
        "morning", "evening", "sunset", "meeting", "journey", "lesson", "walk", "review", "return", "arrival",
    ];

    private readonly ulong seed;

    /// <summary>
    /// Initializes a text factory tied to one corpus seed.
    /// </summary>
    /// <param name="seed">The corpus seed.</param>
    public CorpusTextFactory(ulong seed)
    {
        this.seed = seed;
    }

    /// <summary>
    /// Builds one deterministic candidate with a requested approximate word count.
    /// </summary>
    /// <param name="fixtureIndex">The zero-based fixture index.</param>
    /// <param name="utteranceIndex">The zero-based utterance index in the fixture.</param>
    /// <param name="attempt">The duration-adjustment attempt.</param>
    /// <param name="targetWordCount">The requested approximate word count.</param>
    /// <returns>Natural English text made of complete sentences.</returns>
    public string BuildCandidate(int fixtureIndex, int utteranceIndex, int attempt, int targetWordCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetWordCount, 6);

        var random = new StableRandom(StableRandom.DeriveSeed(
            seed,
            string.Create(CultureInfo.InvariantCulture, $"text/{fixtureIndex}/{utteranceIndex}/{attempt}/{targetWordCount}")));
        var sentences = new List<string>();
        var wordCount = 0;

        while (wordCount < targetWordCount)
        {
            var sentence = BuildSentence(random);
            sentences.Add(sentence);
            wordCount += CountWords(sentence);
        }

        return string.Join(" ", sentences);
    }

    private static string BuildSentence(StableRandom random)
    {
        var adjective = Pick(random, Adjectives);
        var secondAdjective = Pick(random, Adjectives);
        var noun = Pick(random, Nouns);
        var secondNoun = Pick(random, Nouns);
        var item = Pick(random, Objects);
        var secondItem = Pick(random, Objects);
        var place = Pick(random, Places);
        var verb = Pick(random, Verbs);
        var secondVerb = Pick(random, Verbs);
        var adverb = Pick(random, Adverbs);
        var time = Pick(random, TimeWords);

        return random.NextInt(0, 8) switch
        {
            0 => $"The {adjective} {noun} {verb} the {item} {adverb} near the {place}.",
            1 => $"Will the {noun} {verb} the {secondItem} before the {time}?",
            2 => $"Each {noun} can {verb} with a {secondAdjective} {secondNoun} by the {place}.",
            3 => $"The {adjective} {noun} and the {secondNoun} {secondVerb} a {item} together.",
            4 => $"A {secondAdjective} {noun} {verb} the {item} while the {secondNoun} watches.",
            5 => $"The {noun} keeps the {secondItem} ready for a {adjective} walk through the {place}.",
            6 => $"Can a {adjective} {noun} {verb} the {item} after the {time}?",
            _ => $"The {noun} {verb} a {secondAdjective} {secondItem} and shares the plan with the {secondNoun}.",
        };
    }

    private static string Pick(StableRandom random, string[] values)
    {
        return values[random.NextInt(0, values.Length)];
    }

    private static int CountWords(string text)
    {
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }
}

/// <summary>
/// Applies the transcript normalization rules that ASR comparisons consume.
/// </summary>
internal static class TranscriptNormalizer
{
    /// <summary>
    /// Unicode-normalizes, lowercases, removes punctuation, and collapses whitespace.
    /// </summary>
    /// <param name="authoredText">The original authored utterance text.</param>
    /// <returns>The stable expected comparison text.</returns>
    public static string Normalize(string authoredText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authoredText);

        var normalized = authoredText.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;

        foreach (var character in normalized)
        {
            if (char.IsPunctuation(character))
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString().Trim();
    }
}
