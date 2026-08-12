using WinBulkTranscript.Core.Domain;

namespace WinBulkTranscript.Core.Batch;

/// <summary>Snapshots MP4 files without traversing reparse points.</summary>
public static class Mp4Discovery
{
    public static DiscoveryResult Discover(string inputRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputRoot);

        var root = Path.GetFullPath(inputRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The input folder does not exist: {root}");
        }

        try
        {
            if ((new DirectoryInfo(root).Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return new DiscoveryResult(
                    Array.Empty<string>(),
                    [new DiscoveryIssue(root, "The selected input folder is a reparse point and cannot be traversed.")]);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new DiscoveryResult(
                Array.Empty<string>(),
                [new DiscoveryIssue(root, $"Could not inspect selected folder: {exception.Message}")]);
        }

        var files = new List<string>();
        var issues = new List<DiscoveryIssue>();
        var directories = new Stack<string>();
        directories.Push(root);

        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = directories.Pop();

            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    FileAttributes attributes;
                    try
                    {
                        attributes = entry.Attributes;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        issues.Add(new DiscoveryIssue(entry.FullName, $"Could not inspect item: {exception.Message}"));
                        continue;
                    }

                    // Reparse-point directories can create cycles or leave the selected tree. Files are
                    // skipped too so a file link cannot unexpectedly read data outside the input root.
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directories.Push(entry.FullName);
                    }
                    else if (string.Equals(Path.GetExtension(entry.Name), ".mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(entry.FullName);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                issues.Add(new DiscoveryIssue(directory, $"Could not enumerate folder: {exception.Message}"));
            }
        }

        files.Sort((left, right) => CompareRelative(root, left, right));
        return new DiscoveryResult(files, issues);
    }

    /// <summary>Counts MP4s with the same traversal rules as the processing snapshot.</summary>
    public static int Count(string inputRoot, CancellationToken cancellationToken)
        => Discover(inputRoot, cancellationToken).Files.Count;

    private static int CompareRelative(string root, string left, string right)
    {
        var leftRelative = Path.GetRelativePath(root, left);
        var rightRelative = Path.GetRelativePath(root, right);
        var insensitive = StringComparer.OrdinalIgnoreCase.Compare(leftRelative, rightRelative);
        return insensitive != 0 ? insensitive : StringComparer.Ordinal.Compare(leftRelative, rightRelative);
    }
}
