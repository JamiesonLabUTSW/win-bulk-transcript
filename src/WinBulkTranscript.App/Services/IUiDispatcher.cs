namespace WinBulkTranscript.App.Services;

/// <summary>Small dispatcher seam that keeps snapshot mapping explicitly UI-thread safe.</summary>
public interface IUiDispatcher
{
    /// <summary>Gets whether the caller is already on the UI thread.</summary>
    bool HasThreadAccess { get; }

    /// <summary>Queues work for the UI thread.</summary>
    bool TryEnqueue(Action callback);
}
