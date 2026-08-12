using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinBulkTranscript.Core.Domain;
using WinBulkTranscript.Core.Ports;

namespace WinBulkTranscript.App.Services;

/// <summary>Obtains one batch-wide output-collision answer through an accessible WinUI dialog.</summary>
public sealed class ContentDialogExistingOutputPolicyResolver : IExistingOutputPolicyResolver
{
    private readonly Func<XamlRoot?> _xamlRootProvider;
    private readonly IUiDispatcher _dispatcher;
    private readonly ModalDialogCoordinator _dialogs;

    /// <summary>Initializes the resolver with the current window root and dispatcher.</summary>
    public ContentDialogExistingOutputPolicyResolver(
        Func<XamlRoot?> xamlRootProvider,
        IUiDispatcher dispatcher,
        ModalDialogCoordinator dialogs)
    {
        _xamlRootProvider = xamlRootProvider ?? throw new ArgumentNullException(nameof(xamlRootProvider));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
    }

    /// <inheritdoc />
    public Task<ExistingOutputPolicy> ResolveAsync(IReadOnlyList<BatchItem> existingOutputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(existingOutputs);
        cancellationToken.ThrowIfCancellationRequested();

        if (_dispatcher.HasThreadAccess)
        {
            return ShowAsync(existingOutputs, cancellationToken);
        }

        var completion = new TaskCompletionSource<ExistingOutputPolicy>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    completion.TrySetResult(await ShowAsync(existingOutputs, cancellationToken));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("The UI dispatcher is unavailable for the collision dialog."));
        }

        return completion.Task;
    }

    private async Task<ExistingOutputPolicy> ShowAsync(IReadOnlyList<BatchItem> existingOutputs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var xamlRoot = _xamlRootProvider()
            ?? throw new InvalidOperationException("The collision dialog cannot be shown before the main window is loaded.");
        var count = existingOutputs.Count;
        var fileNoun = count == 1 ? "file already has" : "files already have";
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Existing transcript files",
            Content = $"{count} {fileNoun} a matching WebVTT transcript. Choose one policy for the entire batch.",
            PrimaryButtonText = "Skip existing",
            SecondaryButtonText = "Overwrite all",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        AutomationProperties.SetName(dialog, "Existing transcript files");

        var result = await _dialogs.ShowAsync(dialog, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result switch
        {
            ContentDialogResult.Primary => ExistingOutputPolicy.SkipExisting,
            ContentDialogResult.Secondary => ExistingOutputPolicy.OverwriteAll,
            _ => ExistingOutputPolicy.Cancel,
        };
    }

}
