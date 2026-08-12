using WinBulkTranscript.Core.Domain;

namespace WinBulkTranscript.Core.Batch;

/// <summary>Validates stable input-to-output mappings before any model or media work starts.</summary>
public static class BatchPreflight
{
    public static PreflightResult Create(BatchRequest request, IReadOnlyList<string> inputPaths)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(inputPaths);

        var inputRoot = Path.GetFullPath(request.InputRoot);
        var outputRoot = Path.GetFullPath(request.OutputRoot);
        var items = new List<BatchItem>(inputPaths.Count);
        var outputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var inputPath in inputPaths)
        {
            var fullInputPath = Path.GetFullPath(inputPath);
            var relativeInputPath = Path.GetRelativePath(inputRoot, fullInputPath);
            if (IsOutsideRoot(relativeInputPath))
            {
                throw new BatchPreflightException($"Input file escapes the selected input folder: {fullInputPath}");
            }

            var relativeOutputPath = Path.ChangeExtension(relativeInputPath, ".vtt");
            var outputPath = Path.GetFullPath(Path.Combine(outputRoot, relativeOutputPath));
            var outputRelativePath = Path.GetRelativePath(outputRoot, outputPath);
            if (IsOutsideRoot(outputRelativePath))
            {
                throw new BatchPreflightException($"Output mapping escapes the selected output folder: {relativeInputPath}");
            }

            if (!outputPaths.Add(outputPath))
            {
                throw new BatchPreflightException($"Two input files map to the same VTT path: {outputPath}");
            }

            items.Add(new BatchItem(fullInputPath, relativeInputPath, outputPath));
        }

        var existing = new List<BatchItem>();
        foreach (var item in items)
        {
            // A directory at the mapped output path is not an overwriteable VTT. Failing it
            // before model/media work is safer than presenting an overwrite choice that cannot
            // succeed and then reporting an isolated per-file writer failure.
            if (Directory.Exists(item.OutputPath))
            {
                throw new BatchPreflightException($"The mapped VTT output path is an existing directory: {item.OutputPath}");
            }

            if (File.Exists(item.OutputPath))
            {
                existing.Add(item);
            }
        }

        return new PreflightResult(items, existing);
    }

    private static bool IsOutsideRoot(string relativePath)
        => string.Equals(relativePath, "..", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath);
}

public sealed class BatchPreflightException : Exception
{
    public BatchPreflightException(string message)
        : base(message)
    {
    }
}
