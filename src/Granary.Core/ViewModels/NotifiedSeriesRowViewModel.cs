using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Granary.Core.ViewModels;

/// <summary>
/// A series in the Series view: its poster, title, what notifies or why nothing does, its reason pills, and Mute or
/// Unmute. One that does not notify is dimmed.
/// </summary>
public sealed partial class NotifiedSeriesRowViewModel : ObservableObject
{
    internal NotifiedSeriesRowViewModel(string contentId, string title, string line, bool notifies, bool muted, IReadOnlyList<ReasonPill> pills,
        Uri? posterUrl, bool canMute, ICommand openCommand, ICommand muteCommand)
    {
        ContentId = contentId;
        Title = title;
        Line = line;
        Notifies = notifies;
        Muted = muted;
        Pills = pills;
        PosterUrl = posterUrl;
        CanMute = canMute;
        OpenCommand = openCommand;
        MuteCommand = muteCommand;
    }

    public string ContentId { get; }

    public string Title { get; }

    /// <summary>"Any new episode", "Muted", "Watchlist notifications are off".</summary>
    public string Line { get; }

    public bool Notifies { get; }

    public bool Muted { get; }

    public IReadOnlyList<ReasonPill> Pills { get; }

    public bool HasPills => Pills.Count > 0;

    public Uri? PosterUrl { get; }

    /// <summary>The server can mute a series: the row has its button.</summary>
    public bool CanMute { get; }

    /// <summary>"Mute" or "Unmute".</summary>
    public string MuteLabel => Muted ? "Unmute" : "Mute";

    public string MuteTip => Muted ? "Notify me about new episodes again" : "Stop notifying me about new episodes";

    public string MuteName => $"{MuteLabel} {Title}";

    /// <summary>Takes this row.</summary>
    public ICommand OpenCommand { get; }

    /// <summary>Takes this row: mutes it, or unmutes it when muted.</summary>
    public ICommand MuteCommand { get; }

    /// <summary>Its mute is on its way: the button waits.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPressMute))]
    public partial bool IsPending { get; internal set; }

    public bool CanPressMute => !IsPending;

    public string AccessibleName => $"{Title}, {Line}";
}
