using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinBulkTranscript.App.Foundry;
using WinBulkTranscript.App.Media;
using WinBulkTranscript.App.Services;
using WinBulkTranscript.App.ViewModels;
using WinBulkTranscript.Core.Audio;
using WinBulkTranscript.Core.Batch;
using WinBulkTranscript.Core.Output;
using WinBulkTranscript.Core.Ports;

namespace WinBulkTranscript.App.Composition;

/// <summary>Manual composition root; production Windows adapters can be supplied without a DI container.</summary>
public sealed class AppComposition
{
    private readonly ITranscriptionBatchRunner _batchRunner;

    /// <summary>Creates the production composition with fresh Windows adapters for each batch.</summary>
    public AppComposition()
        : this(CreateProductionCoordinator)
    {
    }

    private static BatchTranscriptionCoordinator CreateProductionCoordinator(IExistingOutputPolicyResolver collisionResolver)
    {
        WindowsMediaAudioExtractor.CleanupStaleTemporaryFiles();
        var modelHost = new FoundryLocalModelHost();
        return new BatchTranscriptionCoordinator(
            new WindowsMediaAudioExtractor(),
            new AdaptiveEnergyVoiceActivityDetector(),
            new NemotronSegmentRecognizer(modelHost),
            new WebVttWriter(),
            modelHost,
            collisionResolver);
    }

    /// <summary>Creates a composition with a concrete app-facing runner.</summary>
    public AppComposition(ITranscriptionBatchRunner batchRunner)
    {
        _batchRunner = batchRunner ?? throw new ArgumentNullException(nameof(batchRunner));
    }

    /// <summary>
    /// Creates a bridge directly to the Core coordinator. The supplied factory should create fresh
    /// disposable Windows adapter instances for each batch.
    /// </summary>
    public AppComposition(Func<IExistingOutputPolicyResolver, BatchTranscriptionCoordinator> coordinatorFactory)
        : this(new BatchCoordinatorRunner(coordinatorFactory))
    {
    }

    /// <summary>Creates the window's main view model on the UI thread.</summary>
    public MainViewModel CreateMainViewModel(Func<XamlRoot?> xamlRootProvider)
    {
        ArgumentNullException.ThrowIfNull(xamlRootProvider);
        var dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("The main view model must be created on the UI thread.");
        var dispatcher = new DispatcherQueueUiDispatcher(dispatcherQueue);
        var collisionResolver = new ContentDialogExistingOutputPolicyResolver(xamlRootProvider, dispatcher);
        return new MainViewModel(_batchRunner, collisionResolver, dispatcher);
    }
}
