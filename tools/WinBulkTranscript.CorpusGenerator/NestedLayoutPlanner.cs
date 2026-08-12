namespace WinBulkTranscript.CorpusGenerator;

/// <summary>
/// Creates the deterministic nested copy layout without depending on synthesized media bytes.
/// </summary>
internal static class NestedLayoutPlanner
{
    private const int RequiredCoveragePerDepth = 4;

    private static readonly string[] FolderNames =
    [
        "amber field", "birch-grove", "café lane", "dawn harbor", "elm", "frost path", "glow", "juniper",
        "kind meadow", "lilac", "maple ridge", "north shore", "oak", "paper trail", "quiet room", "river bend",
        "sunny porch", "tulip", "violet walk", "willow",
    ];

    /// <summary>
    /// Produces one reproducible placement for each fixture ID.
    /// </summary>
    /// <param name="seed">The corpus seed.</param>
    /// <param name="fixtureIds">The complete fixture ID set.</param>
    /// <returns>Paths relative to the nested input root.</returns>
    public static IReadOnlyDictionary<string, NestedPlacement> Create(ulong seed, IEnumerable<string> fixtureIds)
    {
        ArgumentNullException.ThrowIfNull(fixtureIds);

        var orderedFixtureIds = fixtureIds.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (orderedFixtureIds.Length != orderedFixtureIds.Distinct(StringComparer.Ordinal).Count())
        {
            throw new ArgumentException("Fixture IDs must be unique.", nameof(fixtureIds));
        }

        if (orderedFixtureIds.Length < RequiredCoveragePerDepth * 3)
        {
            throw new ArgumentException("The corpus needs enough fixtures to force all layout-depth coverage.", nameof(fixtureIds));
        }

        var random = new StableRandom(StableRandom.DeriveSeed(seed, "nested-layout-v1"));
        var depths = new List<int>(orderedFixtureIds.Length);
        for (var depth = 1; depth <= 3; depth++)
        {
            for (var coverageIndex = 0; coverageIndex < RequiredCoveragePerDepth; coverageIndex++)
            {
                depths.Add(depth);
            }
        }

        while (depths.Count < orderedFixtureIds.Length)
        {
            depths.Add(random.NextInt(1, 4));
        }

        random.Shuffle(depths);

        var result = new Dictionary<string, NestedPlacement>(orderedFixtureIds.Length, StringComparer.Ordinal);
        for (var fixtureIndex = 0; fixtureIndex < orderedFixtureIds.Length; fixtureIndex++)
        {
            var folderSegments = new List<string>(depths[fixtureIndex]);
            for (var folderIndex = 0; folderIndex < depths[fixtureIndex]; folderIndex++)
            {
                folderSegments.Add(ChooseFolderName(random, fixtureIndex, folderIndex));
            }

            folderSegments.Add($"{orderedFixtureIds[fixtureIndex]}.mp4");
            var relativePath = string.Join('/', folderSegments);
            result.Add(orderedFixtureIds[fixtureIndex], new NestedPlacement(relativePath, depths[fixtureIndex]));
        }

        return result;
    }

    private static string ChooseFolderName(StableRandom random, int fixtureIndex, int folderIndex)
    {
        // The first two fixture paths intentionally guarantee a space and Unicode path segment. Their depths remain seeded.
        if (folderIndex == 0 && fixtureIndex == 0)
        {
            return "amber field";
        }

        if (folderIndex == 0 && fixtureIndex == 1)
        {
            return "café lane";
        }

        return FolderNames[random.NextInt(0, FolderNames.Length)];
    }
}

/// <summary>
/// Represents a seeded path beneath the nested input root.
/// </summary>
/// <param name="RelativePath">The forward-slash relative path.</param>
/// <param name="Depth">The number of directory segments.</param>
internal sealed record NestedPlacement(string RelativePath, int Depth);
