using Microsoft.UI.Xaml;

namespace Granary.MpvSpike;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(SpikeOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray()));
        _window.Activate();
    }
}
