using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Granary.App.Controls;

/// <summary>A playback notice: over the full player below its top bar, or above the minimized player's bar.</summary>
public sealed partial class PlaybackNoticeView : UserControl
{
    public static readonly DependencyProperty NoticeTitleProperty = DependencyProperty.Register(
        nameof(NoticeTitle), typeof(string), typeof(PlaybackNoticeView), new PropertyMetadata("", (d, _) => ((PlaybackNoticeView)d).Show()));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(PlaybackNoticeView), new PropertyMetadata("", (d, _) => ((PlaybackNoticeView)d).Show()));

    public static readonly DependencyProperty IsWarningProperty = DependencyProperty.Register(
        nameof(IsWarning), typeof(bool), typeof(PlaybackNoticeView), new PropertyMetadata(false, (d, _) => ((PlaybackNoticeView)d).Show()));

    private static readonly SolidColorBrush InfoTint = new(Color.FromArgb(0x26, 0x0E, 0xA5, 0xE9));
    private static readonly SolidColorBrush InfoEdge = new(Color.FromArgb(0x80, 0x38, 0xBD, 0xF8));
    private static readonly SolidColorBrush WarningTint = new(Color.FromArgb(0x26, 0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush WarningEdge = new(Color.FromArgb(0x80, 0xFB, 0xBF, 0x24));

    public PlaybackNoticeView()
    {
        InitializeComponent();
        Show();
    }

    public string NoticeTitle
    {
        get => (string)GetValue(NoticeTitleProperty);
        set => SetValue(NoticeTitleProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>Amber rather than sky blue: the server is going away.</summary>
    public bool IsWarning
    {
        get => (bool)GetValue(IsWarningProperty);
        set => SetValue(IsWarningProperty, value);
    }

    private void Show()
    {
        TitleText.Text = NoticeTitle ?? "";
        MessageText.Text = Message ?? "";
        Frame.Background = IsWarning ? WarningTint : InfoTint;
        Frame.BorderBrush = IsWarning ? WarningEdge : InfoEdge;
    }
}
