using WinBulkTranscript.Core.Domain;
using WinBulkTranscript.Core.Ports;

namespace WinBulkTranscript.App.Composition;

/// <summary>App-facing bridge to the Core coordinator, kept free of Windows adapter details.</summary>
public interface ITranscriptionBatchRunner
{
    /// <summary>Runs one complete batch and reports immutable Core snapshots.</summary>
    Task RunAsync(
        BatchRequest request,
        IProgress<BatchProgressSnapshot>? progress,
        IExistingOutputPolicyResolver existingOutputPolicyResolver,
        CancellationToken cancellationToken);
}
