using WinBulkTranscript.Core.Batch;
using WinBulkTranscript.Core.Domain;

namespace WinBulkTranscript.Core.Tests;

public sealed class Mp4DiscoveryTests
{
    [Fact]
    public void Discover_RecursesForMp4FilesOnly_AndSortsByRelativePath()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var alpha = workspace.CreateTextFile(Path.Combine("input", "alpha.MP4"));
        var beta = workspace.CreateTextFile(Path.Combine("input", "nested", "beta.mp4"));
        var gamma = workspace.CreateTextFile(Path.Combine("input", "nested", "deeper", "gamma.Mp4"));
        workspace.CreateTextFile(Path.Combine("input", "ignore.mov"));
        workspace.CreateTextFile(Path.Combine("input", "nested", "ignore.vtt"));

        var result = Mp4Discovery.Discover(inputRoot, CancellationToken.None);

        Assert.Equal([alpha, beta, gamma], result.Files);
        Assert.Empty(result.Issues);
        Assert.Equal(3, Mp4Discovery.Count(inputRoot, CancellationToken.None));
    }

    [Fact]
    public void Discover_UsesOrdinalIgnoreCaseRelativeOrderingWithOrdinalTieBreak()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var top = workspace.CreateTextFile(Path.Combine("input", "B.mp4"));
        var nested = workspace.CreateTextFile(Path.Combine("input", "a", "z.mp4"));
        var lower = workspace.CreateTextFile(Path.Combine("input", "a.mp4"));

        var result = Mp4Discovery.Discover(inputRoot, CancellationToken.None);

        Assert.Equal([lower, nested, top], result.Files);
    }

    [Fact]
    public void Discover_RejectsMissingRootAndHonorsPreCancelledToken()
    {
        using var workspace = new TestWorkspace();
        Assert.Throws<DirectoryNotFoundException>(() => Mp4Discovery.Discover(Path.Combine(workspace.Root, "missing"), CancellationToken.None));

        var inputRoot = workspace.CreateDirectory("input");
        workspace.CreateTextFile(Path.Combine("input", "one.mp4"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Mp4Discovery.Discover(inputRoot, cancellation.Token));
    }
}

public sealed class BatchPreflightTests
{
    [Fact]
    public void Create_PreservesRelativeDirectoriesAndFindsExistingOutputs()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var outputRoot = workspace.CreateDirectory("output");
        var top = workspace.CreateTextFile(Path.Combine("input", "one.mp4"));
        var nested = workspace.CreateTextFile(Path.Combine("input", "nested", "two.MP4"));
        var existingOutput = workspace.CreateTextFile(Path.Combine("output", "nested", "two.vtt"), "existing");
        var request = BatchRequest.Create(inputRoot, outputRoot);

        var result = BatchPreflight.Create(request, [top, nested]);

        Assert.Collection(
            result.Items,
            first =>
            {
                Assert.Equal(top, first.InputPath);
                Assert.Equal("one.mp4", first.RelativePath);
                Assert.Equal(Path.Combine(outputRoot, "one.vtt"), first.OutputPath);
            },
            second =>
            {
                Assert.Equal(nested, second.InputPath);
                Assert.Equal(Path.Combine("nested", "two.MP4"), second.RelativePath);
                Assert.Equal(existingOutput, second.OutputPath);
            });
        Assert.Equal([result.Items[1]], result.ExistingOutputs);
    }

    [Fact]
    public void Create_RejectsInputOutsideRootAndOutputCollisions()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var outputRoot = workspace.CreateDirectory("output");
        var inside = workspace.CreateTextFile(Path.Combine("input", "same.mp4"));
        var outside = workspace.CreateTextFile("outside.mp4");
        var request = BatchRequest.Create(inputRoot, outputRoot);

        var outsideException = Assert.Throws<BatchPreflightException>(() => BatchPreflight.Create(request, [inside, outside]));
        Assert.Contains("escapes", outsideException.Message, StringComparison.OrdinalIgnoreCase);

        var collisionException = Assert.Throws<BatchPreflightException>(() => BatchPreflight.Create(request, [inside, inside]));
        Assert.Contains("same VTT", collisionException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_RejectsSiblingWithSharedRootPrefixAndCaseOnlyOutputCollision()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var siblingRoot = workspace.CreateDirectory("input-sibling");
        var outputRoot = workspace.CreateDirectory("output");
        var inside = workspace.CreateTextFile(Path.Combine("input", "clip.mp4"));
        var sibling = workspace.CreateTextFile(Path.Combine("input-sibling", "clip.mp4"));
        var request = BatchRequest.Create(inputRoot, outputRoot);

        var outsideException = Assert.Throws<BatchPreflightException>(() => BatchPreflight.Create(request, [inside, sibling]));
        Assert.Contains("escapes", outsideException.Message, StringComparison.OrdinalIgnoreCase);

        var caseOnlyInput = Path.Combine(inputRoot, "CLIP.MP4");
        var collisionException = Assert.Throws<BatchPreflightException>(() => BatchPreflight.Create(request, [inside, caseOnlyInput]));
        Assert.Contains("same VTT", collisionException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_AllowsInputAndOutputToBeTheSameDirectoryBecauseOnlyMp4InputsAreMapped()
    {
        using var workspace = new TestWorkspace();
        var root = workspace.CreateDirectory("shared");
        var input = workspace.CreateTextFile(Path.Combine("shared", "clip.mp4"));
        var request = BatchRequest.Create(root, root);

        var result = BatchPreflight.Create(request, [input]);

        var item = Assert.Single(result.Items);
        Assert.Equal(Path.Combine(root, "clip.vtt"), item.OutputPath);
        Assert.Empty(result.ExistingOutputs);
    }

    [Fact]
    public void Create_RejectsDirectoryAtMappedOutputPathBeforeProcessing()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var outputRoot = workspace.CreateDirectory("output");
        var input = workspace.CreateTextFile(Path.Combine("input", "clip.mp4"));
        Directory.CreateDirectory(Path.Combine(outputRoot, "clip.vtt"));

        var exception = Assert.Throws<BatchPreflightException>(
            () => BatchPreflight.Create(BatchRequest.Create(inputRoot, outputRoot), [input]));

        Assert.Contains("existing directory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
