using Granary.Core.ViewModels;
using Granary.Player;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

public sealed partial class PlayerPage : Page
{
    // The seek bar follows playback; a jump larger than this is the user dragging it.
    private const double UserSeekThresholdSeconds = 1.0;

    public PlayerPage()
    {
        ViewModel = App.Services.GetRequiredService<PlayerViewModel>();
        InitializeComponent();
        VideoHost.VideoOutput = App.Services.GetRequiredService<ICompositionVideoOutput>();
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public PlayerViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string source && source.Length > 0)
        {
            ViewModel.Source = source;
            ViewModel.OpenCommand.Execute(null);
        }
    }

    private void OnSeekBarValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (Math.Abs(e.NewValue - ViewModel.PositionSeconds) > UserSeekThresholdSeconds)
        {
            ViewModel.SeekTo(e.NewValue);
        }
    }

    private void OnPlayPauseInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is not TextBox)
        {
            ViewModel.TogglePauseCommand.Execute(null);
            args.Handled = true;
        }
    }

    // Instance methods because x:Bind function bindings call them on the page.
    public string PlayPauseGlyph(bool isPaused) => isPaused ? "" : "";

    public string TimeText(double position, double duration) => $"{Format(position)} / {Format(duration)}";

    private static string Format(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }
}
