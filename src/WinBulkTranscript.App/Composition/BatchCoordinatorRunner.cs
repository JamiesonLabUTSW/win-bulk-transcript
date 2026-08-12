using WinBulkTranscript.Core.Batch;
using WinBulkTranscript.Core.Domain;
using WinBulkTranscript.Core.Ports;

namespace WinBulkTranscript.App.Composition;

/// <summary>Adapts the Core coordinator to the app runner seam used by the view model.</summary>
public sealed class BatchCoordinatorRunner : ITranscriptionBatchRunner
{
    private readonly Func<IExistingOutputPolicyResolver, BatchTranscriptionCoordinator> _coordinatorFactory;

    /// <summary>Initializes the runner with a factory for the fully composed Core coordinator.</summary>
    public BatchCoordinatorRunner(Func<IExistingOutputPolicyResolver, BatchTranscriptionCoordinator> coordinatorFactory)
    {
        _coordinatorFactory = coordinatorFactory ?? throw new ArgumentNullException(nameof(coordinatorFactory));
    }

    /// <inheritdoc />
    public Task RunAsync(
        BatchRequest request,
        IProgress<BatchProgressSnapshot>? progress,
        IExistingOutputPolicyResolver existingOutputPolicyResolver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(existingOutputPolicyResolver);
        // Run the coordinator's synchronous preflight (directory traversal, collision discovery,
        // and model construction) on a worker. Progress and the collision resolver both marshal
        // back through their UI-safe boundaries, so this never makes WinUI controls thread-affine.
        return Task.Run(
            () => _coordinatorFactory(existingOutputPolicyResolver)
                .RunAsync(request, progress, cancellationToken),
            cancellationToken);
    }
}
