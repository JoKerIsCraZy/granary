using CommunityToolkit.Mvvm.ComponentModel;

namespace Granary.Core.ViewModels;

/// <summary>
/// One of the account's requests on the Yours tab, as the web's row: the poster, the title with its state, "Series · 2024
/// · Seasons 1–3", the download's progress, when it was requested and changed, and what went wrong.
/// </summary>
public sealed partial class RequestRowViewModel : ObservableObject
{
    internal RequestRowViewModel(string id, string title)
    {
        Id = id;
        Title = title;
    }

    public string Id { get; }

    public string Title { get; }

    public Uri? PosterUrl { get; init; }

    /// <summary>"Pending", "Available", …; empty when the request has no state the pages know.</summary>
    public string StateLabel { get; init; } = "";

    public RequestBadgeLook StateLook { get; init; }

    public bool HasState => StateLabel.Length > 0;

    /// <summary>"Series · 2024 · Seasons 1–3 · 14 of 20 episodes in the library".</summary>
    public string Details { get; init; } = "";

    /// <summary>"Downloading · 43% · about 12 min left"; empty without a download.</summary>
    public string DownloadText { get; init; } = "";

    public bool HasDownload => DownloadText.Length > 0;

    /// <summary>Null while the size is unknown: the bar shows only with a figure.</summary>
    public double? DownloadPercent { get; init; }

    public bool HasDownloadBar => DownloadPercent is not null;

    public double DownloadValue => DownloadPercent ?? 0;

    /// <summary>"Requested Oct 5, 2026 · Updated 3h ago".</summary>
    public string Dates { get; init; } = "";

    /// <summary>What went wrong (in red), or the admin's reason ("Reason: …"); empty for neither.</summary>
    public string Note { get; init; } = "";

    public bool IsError { get; init; }

    public bool HasNote => Note.Length > 0;

    /// <summary>The library title once it arrived: Open in library.</summary>
    public string? LibraryContentId { get; init; }

    public bool CanOpenInLibrary => LibraryContentId is not null;

    /// <summary>Withdrawable: pending, or approved with nothing sent yet.</summary>
    public bool CanCancel { get; init; }

    [ObservableProperty]
    public partial bool IsCancelling { get; internal set; }

    public string CancelName => $"Cancel request for {Title}";

    public string OpenName => $"Open {Title} in library";
}

/// <summary>Requests sharing what happens next: "Needs attention 2".</summary>
public sealed record RequestGroupViewModel(string Title, IReadOnlyList<RequestRowViewModel> Rows)
{
    public string Count => Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A row of the status help: the badge and what it means.</summary>
public sealed record RequestStatusHelp(string Label, RequestBadgeLook Look, string Description);
