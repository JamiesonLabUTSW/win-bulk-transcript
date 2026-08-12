using System.Text;

namespace WinBulkTranscript.CorpusGenerator;

/// <summary>
/// A small, explicitly specified SplitMix64 random source used to keep corpus choices stable across runtime versions.
/// </summary>
internal sealed class StableRandom
{
    private ulong state;

    /// <summary>
    /// Initializes a random source with the supplied seed.
    /// </summary>
    /// <param name="seed">The stable seed.</param>
    public StableRandom(ulong seed)
    {
        state = seed;
    }

    /// <summary>
    /// Produces an unsigned 64-bit value.
    /// </summary>
    /// <returns>A pseudorandom value.</returns>
    public ulong NextUInt64()
    {
        state += 0x9E37_79B9_7F4A_7C15UL;
        var value = state;
        value = (value ^ (value >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D0_49BB_1331_11EBUL;
        return value ^ (value >> 31);
    }

    /// <summary>
    /// Produces an unbiased integer in a half-open range.
    /// </summary>
    /// <param name="minInclusive">The inclusive lower bound.</param>
    /// <param name="maxExclusive">The exclusive upper bound.</param>
    /// <returns>A random integer in the requested range.</returns>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(minInclusive, maxExclusive, nameof(maxExclusive));

        var range = checked((uint)(maxExclusive - minInclusive));
        return checked(minInclusive + (int)NextBelow(range));
    }

    /// <summary>
    /// Produces an unbiased integer in an inclusive long range.
    /// </summary>
    /// <param name="minInclusive">The inclusive lower bound.</param>
    /// <param name="maxInclusive">The inclusive upper bound.</param>
    /// <returns>A random integer in the requested range.</returns>
    public long NextInt64(long minInclusive, long maxInclusive)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minInclusive, maxInclusive, nameof(maxInclusive));

        var range = checked((ulong)(maxInclusive - minInclusive) + 1UL);
        if (range == 0UL)
        {
            return unchecked((long)NextUInt64());
        }

        return checked(minInclusive + (long)NextBelow(range));
    }

    /// <summary>
    /// Produces a uniformly distributed double in the interval [0, 1).
    /// </summary>
    /// <returns>A pseudorandom fraction.</returns>
    public double NextUnitDouble()
    {
        return (NextUInt64() >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>
    /// Shuffles a list with the stable Fisher-Yates algorithm.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="items">The list to shuffle.</param>
    public void Shuffle<T>(IList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        for (var index = items.Count - 1; index > 0; index--)
        {
            var other = NextInt(0, index + 1);
            (items[index], items[other]) = (items[other], items[index]);
        }
    }

    /// <summary>
    /// Derives a separate deterministic substream from a corpus seed and textual scope.
    /// </summary>
    /// <param name="seed">The corpus seed.</param>
    /// <param name="scope">A stable scope label.</param>
    /// <returns>A derived seed.</returns>
    public static ulong DeriveSeed(ulong seed, string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var hash = 14_695_981_039_346_656_037UL;
        foreach (var value in Encoding.UTF8.GetBytes(scope))
        {
            hash ^= value;
            hash *= 1_099_511_628_211UL;
        }

        var mixer = new StableRandom(seed ^ hash);
        return mixer.NextUInt64();
    }

    private ulong NextBelow(ulong exclusiveMaximum)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(exclusiveMaximum, 0UL);

        var threshold = unchecked(0UL - exclusiveMaximum) % exclusiveMaximum;
        while (true)
        {
            var candidate = NextUInt64();
            if (candidate >= threshold)
            {
                return candidate % exclusiveMaximum;
            }
        }
    }
}
