using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using WinBulkTranscript.App.Services;
using WinBulkTranscript.App.ViewModels;

namespace WinBulkTranscript.App;

/// <summary>Thin view responsible for AppWindow-aware folder pickers.</summary>
public sealed partial class MainPage : Page
{
    private readonly DispatcherTimer _timingTimer;
    private MainViewModel? _viewModel;
    private LegalDialogController? _legalDialogs;

    public MainPage()
    {
        InitializeComponent();
        _timingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timingTimer.Tick += (_, _) => _viewModel?.RefreshTimingDisplay();
        _timingTimer.Start();
    }

    /// <summary>Connects the page to its view model after the window has been composed.</summary>
    public void Initialize(MainViewModel viewModel, LegalDialogController legalDialogs)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _legalDialogs = legalDialogs ?? throw new ArgumentNullException(nameof(legalDialogs));
        DataContext = _viewModel;
    }

    /// <summary>
    /// Restores a predictable keyboard target after a batch. The queue turn lets the asynchronous
    /// command finish re-enabling Start before focus is moved.
    /// </summary>
    internal void RestoreFocusAfterBatch()
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (StartButton.IsEnabled)
            {
                StartButton.Focus(FocusState.Programmatic);
            }
        });
    }

    private async void BrowseInputButton_Click(object sender, RoutedEventArgs e)
    {
        await PickFolderAsync(isInput: true);
        BrowseInputButton.Focus(FocusState.Programmatic);
    }

    private async void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        await PickFolderAsync(isInput: false);
        BrowseOutputButton.Focus(FocusState.Programmatic);
    }

    private async void LicenseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_legalDialogs is null)
        {
            return;
        }

        try
        {
            await _legalDialogs.ShowLegalInformationAsync();
        }
        finally
        {
            LicenseButton.Focus(FocusState.Programmatic);
        }
    }

    private async Task PickFolderAsync(bool isInput)
    {
        if (_viewModel is null)
        {
            return;
        }

        var app = (App)Application.Current;
        var picker = new FolderPicker(app.MainWindow.AppWindow.Id);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        if (isInput)
        {
            await _viewModel.SetInputFolderAsync(folder.Path);
        }
        else
        {
            await _viewModel.SetOutputFolderAsync(folder.Path);
        }
    }
}
