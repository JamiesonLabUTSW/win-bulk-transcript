using Microsoft.UI.Xaml;
using WinBulkTranscript.App.Composition;

namespace WinBulkTranscript.App;

/// <summary>Application entry point and composition root for the single-window experience.</summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    /// <summary>Gets the application's only top-level window.</summary>
    public MainWindow MainWindow { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var composition = new AppComposition();
        MainWindow = new MainWindow(composition.CreateMainViewModel(() => MainWindow?.ContentXamlRoot));
        MainWindow.Activate();
    }
}
