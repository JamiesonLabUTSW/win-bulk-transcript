using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace WinBulkTranscript.App;

/// <summary>Responsive in-window modal for startup acknowledgement and legal documents.</summary>
public sealed partial class LegalOverlay : UserControl
{
    private TaskCompletionSource<ContentDialogResult>? _completion;
    private bool _canReturnToAcknowledgement;

    public LegalOverlay()
    {
        InitializeComponent();
    }

    public Task<ContentDialogResult> ShowStartupAcknowledgementAsync(
        string licenseText,
        string noticesText,
        string noticesStatus)
    {
        SetDocuments(licenseText, noticesText, noticesStatus);
        _canReturnToAcknowledgement = true;
        ShowAcknowledgement();
        return ShowAsync(AcceptButton);
    }

    public Task<ContentDialogResult> ShowLegalInformationAsync(
        string licenseText,
        string noticesText,
        string noticesStatus)
    {
        SetDocuments(licenseText, noticesText, noticesStatus);
        _canReturnToAcknowledgement = false;
        ShowLegalDocuments();
        return ShowAsync(CloseButton);
    }

    public void Dismiss()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(Dismiss);
            return;
        }

        Complete(ContentDialogResult.None);
    }

    private Task<ContentDialogResult> ShowAsync(Control initialFocus)
    {
        if (_completion is not null)
        {
            throw new InvalidOperationException("The legal overlay is already open.");
        }

        _completion = new TaskCompletionSource<ContentDialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Visibility = Visibility.Visible;
        _ = DispatcherQueue.TryEnqueue(() => initialFocus.Focus(FocusState.Programmatic));
        return _completion.Task;
    }

    private void SetDocuments(string licenseText, string noticesText, string noticesStatus)
    {
        LicenseDocument.Text = licenseText;
        NoticesDocument.Text = noticesText;
        AutomationProperties.SetItemStatus(LicenseDocument, "Document loaded.");
        AutomationProperties.SetItemStatus(NoticesDocument, noticesStatus);
        LicenseScroller.ChangeView(null, 0, null, true);
        NoticesScroller.ChangeView(null, 0, null, true);
        LegalTabs.SelectedIndex = 0;
    }

    private void ShowAcknowledgement()
    {
        HeadingText.Text = "Academic Research Use Acknowledgement";
        AutomationProperties.SetName(HeadingText, "Academic Research Use Acknowledgement");
        AcknowledgementPanel.Visibility = Visibility.Visible;
        LegalPanel.Visibility = Visibility.Collapsed;
        AcknowledgementActions.Visibility = Visibility.Visible;
        LegalActions.Visibility = Visibility.Collapsed;
    }

    private void ShowLegalDocuments()
    {
        HeadingText.Text = "License and Third-Party Notices";
        AutomationProperties.SetName(HeadingText, "License and Third-Party Notices");
        AcknowledgementPanel.Visibility = Visibility.Collapsed;
        LegalPanel.Visibility = Visibility.Visible;
        AcknowledgementActions.Visibility = Visibility.Collapsed;
        LegalActions.Visibility = Visibility.Visible;
        BackButton.Visibility = _canReturnToAcknowledgement ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Visibility = _canReturnToAcknowledgement ? Visibility.Collapsed : Visibility.Visible;
        LicenseScroller.ChangeView(null, 0, null, true);
        NoticesScroller.ChangeView(null, 0, null, true);
    }

    private void Complete(ContentDialogResult result)
    {
        var completion = _completion;
        if (completion is null)
        {
            return;
        }

        _completion = null;
        Visibility = Visibility.Collapsed;
        completion.TrySetResult(result);
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e) =>
        Complete(ContentDialogResult.Primary);

    private void DeclineButton_Click(object sender, RoutedEventArgs e) =>
        Complete(ContentDialogResult.None);

    private void ViewLicenseButton_Click(object sender, RoutedEventArgs e)
    {
        ShowLegalDocuments();
        _ = DispatcherQueue.TryEnqueue(() => BackButton.Focus(FocusState.Programmatic));
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        ShowAcknowledgement();
        _ = DispatcherQueue.TryEnqueue(() => AcceptButton.Focus(FocusState.Programmatic));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Complete(ContentDialogResult.None);

    private void Overlay_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Complete(ContentDialogResult.None);
        }
    }
}
