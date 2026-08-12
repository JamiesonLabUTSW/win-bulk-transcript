using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinBulkTranscript.App.ViewModels;

namespace WinBulkTranscript.App;

/// <summary>Hosts the single transcription page and coordinates graceful close requests.</summary>
public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _allowClose;
    private bool _closeAfterCancellation;
    private bool _closeDialogOpen;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        Title = "Bulk Transcript";
        AppWindow.Resize(new SizeInt32(1180, 760));
        RootPage.Initialize(_viewModel);
        AppWindow.Closing += OnAppWindowClosing;
        _viewModel.BatchFinished += OnBatchFinished;
    }

    /// <summary>Gets the page's Xaml root for picker and dialog integration.</summary>
    public XamlRoot? ContentXamlRoot => (Content as FrameworkElement)?.XamlRoot;

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || !_viewModel.IsRunning)
        {
            return;
        }

        args.Cancel = true;
        if (_closeDialogOpen || _closeAfterCancellation)
        {
            return;
        }

        _closeDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = ContentXamlRoot,
                Title = "Cancel transcription and close?",
                Content = "The current file will be cancelled. The app waits briefly for bounded cleanup before the window closes.",
                PrimaryButtonText = "Cancel batch and close",
                CloseButtonText = "Keep working",
                DefaultButton = ContentDialogButton.Close,
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                _closeAfterCancellation = true;
                if (_viewModel.IsRunning)
                {
                    _viewModel.RequestCancellation();
                }
                else
                {
                    // The batch can finish while this asynchronous dialog is open. In that
                    // case BatchFinished has already fired, so finish the requested close now.
                    CloseWhenReady();
                }
            }
        }
        finally
        {
            _closeDialogOpen = false;
        }
    }

    private void OnBatchFinished(object? sender, EventArgs args)
    {
        if (_closeAfterCancellation)
        {
            CloseWhenReady();
            return;
        }

        // A cancellation leaves keyboard focus on the now-disabled Cancel button. Restore it to
        // the re-enabled primary action once the async command has completed its state cleanup.
        RootPage.RestoreFocusAfterBatch();
    }

    private void CloseWhenReady()
    {
        _allowClose = true;
        Close();
    }
}
