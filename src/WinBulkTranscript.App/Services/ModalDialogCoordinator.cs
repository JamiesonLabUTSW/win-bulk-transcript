using Microsoft.UI.Xaml.Controls;

namespace WinBulkTranscript.App.Services;

/// <summary>Serializes all modal dialogs owned by the application's single Xaml root.</summary>
public sealed class ModalDialogCoordinator
{
    private readonly object _sync = new();
    private readonly LinkedList<DialogRequest> _pending = new();
    private DialogRequest? _activeRequest;
    private bool _isPumping;

    public Task<ContentDialogResult> ShowAsync(
        ContentDialog dialog,
        bool highPriority = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        return Enqueue(
            new DialogRequest(
                async () => await dialog.ShowAsync(),
                () => TryHide(dialog),
                cancellationToken),
            highPriority);
    }

    public Task<ContentDialogResult> ShowCustomAsync(
        Func<Task<ContentDialogResult>> showAsync,
        Action dismiss,
        bool highPriority = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(showAsync);
        ArgumentNullException.ThrowIfNull(dismiss);
        return Enqueue(new DialogRequest(showAsync, dismiss, cancellationToken), highPriority);
    }

    private Task<ContentDialogResult> Enqueue(DialogRequest request, bool highPriority)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (highPriority)
            {
                _pending.AddFirst(request);
            }
            else
            {
                _pending.AddLast(request);
            }

            if (!_isPumping)
            {
                _isPumping = true;
                _ = PumpAsync();
            }
        }

        return request.Completion.Task;
    }

    public void DismissActiveDialog()
    {
        DialogRequest? request;
        lock (_sync)
        {
            request = _activeRequest;
        }

        request?.Dismiss();
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            DialogRequest? request;
            lock (_sync)
            {
                request = TakeNextRequest();
                if (request is null)
                {
                    _isPumping = false;
                    return;
                }

                _activeRequest = request;
            }

            try
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                using var registration = request.CancellationToken.Register(request.Dismiss);
                var result = await request.ShowAsync();
                request.CancellationToken.ThrowIfCancellationRequested();
                request.Completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
            {
                request.Completion.TrySetCanceled(request.CancellationToken);
            }
            catch (Exception exception)
            {
                request.Completion.TrySetException(exception);
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_activeRequest, request))
                    {
                        _activeRequest = null;
                    }
                }
            }
        }
    }

    private DialogRequest? TakeNextRequest()
    {
        while (_pending.First is not null)
        {
            var request = _pending.First.Value;
            _pending.RemoveFirst();
            if (!request.CancellationToken.IsCancellationRequested)
            {
                return request;
            }

            request.Completion.TrySetCanceled(request.CancellationToken);
        }

        return null;
    }

    private static void TryHide(ContentDialog dialog)
    {
        try
        {
            if (dialog.DispatcherQueue.HasThreadAccess)
            {
                dialog.Hide();
            }
            else
            {
                dialog.DispatcherQueue.TryEnqueue(dialog.Hide);
            }
        }
        catch (InvalidOperationException)
        {
            // The dialog may already be closing because of a user action.
        }
    }

    private sealed record DialogRequest(
        Func<Task<ContentDialogResult>> ShowAsync,
        Action Dismiss,
        CancellationToken CancellationToken)
    {
        public TaskCompletionSource<ContentDialogResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
