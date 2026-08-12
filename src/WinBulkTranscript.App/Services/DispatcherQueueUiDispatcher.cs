using Microsoft.UI.Dispatching;

namespace WinBulkTranscript.App.Services;

/// <summary>WinUI implementation of the UI dispatcher seam.</summary>
public sealed class DispatcherQueueUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _dispatcherQueue;

    /// <summary>Initializes a dispatcher around a WinUI dispatcher queue.</summary>
    public DispatcherQueueUiDispatcher(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
    }

    /// <inheritdoc />
    public bool HasThreadAccess => _dispatcherQueue.HasThreadAccess;

    /// <inheritdoc />
    public bool TryEnqueue(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return _dispatcherQueue.TryEnqueue(() => callback());
    }
}
