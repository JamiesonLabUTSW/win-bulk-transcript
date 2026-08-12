namespace WinBulkTranscript.App.Foundry;

/// <summary>Defines the model candidate shared by the product host and Phase 0 evidence process.</summary>
public static class FoundryModelContract
{
    /// <summary>
    /// Gets the exact CPU variant that remains a candidate until both architecture acceptance reports are retained.
    /// </summary>
    public const string InitialCandidateModelVariant = "nemotron-speech-streaming-en-0.6b-generic-cpu:3";
}
