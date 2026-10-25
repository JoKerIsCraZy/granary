using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Granary.Core.ViewModels;

/// <summary>
/// An inbox entry: its poster, title, how long ago, what happened and why the profile heard of it. Reading it only
/// changes <see cref="IsUnread"/>, so the list stays as it is.
/// </summary>
public sealed partial class NotificationRowViewModel : ObservableObject
{
    internal NotificationRowViewModel(string id, string title, string description, string when, IReadOnlyList<string> reasons, Uri? posterUrl,
        string? opens, bool isUnread, ICommand openCommand, ICommand markReadCommand)
    {
        Id = id;
        Title = title;
        Description = description;
        When = when;
        Reasons = reasons;
        PosterUrl = posterUrl;
        Opens = opens;
        IsUnread = isUnread;
        OpenCommand = openCommand;
        MarkReadCommand = markReadCommand;
    }

    public string Id { get; }

    public string Title { get; }

    /// <summary>"S2E1 — Hello", "Your requested movie is now available".</summary>
    public string Description { get; }

    /// <summary>"5m ago".</summary>
    public string When { get; }

    /// <summary>"Favorite", "Watchlist", … as pills.</summary>
    public IReadOnlyList<string> Reasons { get; }

    public bool HasReasons => Reasons.Count > 0;

    public Uri? PosterUrl { get; }

    /// <summary>The catalog item the entry opens; null when it has none (a request not in a library yet).</summary>
    public string? Opens { get; }

    public bool CanOpen => Opens is not null;

    /// <summary>Takes this row.</summary>
    public ICommand OpenCommand { get; }

    /// <summary>Takes this row.</summary>
    public ICommand MarkReadCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial bool IsUnread { get; internal set; }

    public string AccessibleName => $"{(IsUnread ? "Unread: " : "")}{Title}, {Description}, {When}";
}
