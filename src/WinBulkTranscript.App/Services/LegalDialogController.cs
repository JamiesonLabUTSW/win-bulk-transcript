using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinBulkTranscript.App.Services;

/// <summary>Loads legal documents and coordinates the responsive in-window legal overlay.</summary>
public sealed class LegalDialogController
{
    private const string EmbeddedLicenseName = "WinBulkTranscript.App.Embedded.LICENSE";
    private const string MissingNoticesMessage =
        "Third-party notices are not available in this development build or installation.";

    private readonly LegalOverlay _overlay;
    private readonly Control _underlyingContent;
    private readonly ModalDialogCoordinator _dialogs;

    public LegalDialogController(
        LegalOverlay overlay,
        Control underlyingContent,
        ModalDialogCoordinator dialogs)
    {
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _underlyingContent = underlyingContent
            ?? throw new ArgumentNullException(nameof(underlyingContent));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
    }

    public async Task<bool> ShowStartupAcknowledgementAsync()
    {
        _underlyingContent.IsEnabled = false;
        var notices = ReadAdjacentDocument("THIRD-PARTY-NOTICES.md");
        var result = await _dialogs.ShowCustomAsync(
            () => _overlay.ShowStartupAcknowledgementAsync(
                ReadApplicationLicense(),
                notices ?? MissingNoticesMessage,
                notices is null ? MissingNoticesMessage : "Document loaded."),
            _overlay.Dismiss);
        return result == ContentDialogResult.Primary;
    }

    public async Task ShowLegalInformationAsync()
    {
        var wasEnabled = _underlyingContent.IsEnabled;
        _underlyingContent.IsEnabled = false;
        try
        {
            var notices = ReadAdjacentDocument("THIRD-PARTY-NOTICES.md");
            await _dialogs.ShowCustomAsync(
                () => _overlay.ShowLegalInformationAsync(
                    ReadApplicationLicense(),
                    notices ?? MissingNoticesMessage,
                    notices is null ? MissingNoticesMessage : "Document loaded."),
                _overlay.Dismiss);
        }
        finally
        {
            _underlyingContent.IsEnabled = wasEnabled;
        }
    }

    private static string ReadApplicationLicense()
    {
        var adjacent = ReadAdjacentDocument("LICENSE");
        if (adjacent is not null)
        {
            return adjacent;
        }

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedLicenseName)
            ?? throw new InvalidOperationException("The embedded application license is unavailable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? ReadAdjacentDocument(string fileName)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, fileName);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
