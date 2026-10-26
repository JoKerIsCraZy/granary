using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Requests;

namespace Granary.Core.ViewModels;

/// <summary>A season in "Request seasons": its switch, its name, "2022 · 9 episodes" and where it stands.</summary>
public sealed partial class RequestSeasonChoice : ObservableObject
{
    internal RequestSeasonChoice(RequestMediaSeason season, bool isChoosable, bool isPicked, DateOnly today)
    {
        SeasonNumber = season.SeasonNumber;
        Name = TitleText.SeasonName(season);
        Meta = TitleText.SeasonMeta(season);
        Status = TitleText.SeasonStatus(season, today) ?? "";
        IsChoosable = isChoosable;
        IsPicked = isPicked;
    }

    public int SeasonNumber { get; }

    public string Name { get; }

    public string Meta { get; }

    public string Status { get; }

    /// <summary>Seasons in the library or already asked for show, switched off and greyed, or on for a requested one.</summary>
    public bool IsChoosable { get; }

    [ObservableProperty]
    public partial bool IsPicked { get; set; }

    public string AccessibleName => $"{Name}, {Meta}{(Status.Length > 0 ? ", " + Status : "")}";
}

/// <summary>
/// "Request seasons", as the web's dialog (<c>RequestSeasonsDialog.tsx</c>): seasons in the library or already asked
/// for show but cannot be picked; the aired seasons still missing start picked. A request the viewer does not change
/// names no seasons and leaves the choice to the server, which picks the same ones by its own clock, or the whole series
/// when none has aired.
/// </summary>
public sealed partial class RequestSeasonsViewModel : ObservableObject
{
    private readonly RequestMediaDetail _detail;
    private readonly IReadOnlyList<int> _defaults;
    private readonly int? _latest;
    private readonly IReadOnlyList<int> _upcoming;
    private bool _touched;
    private bool _setting;

    internal RequestSeasonsViewModel(RequestMediaDetail detail, DateOnly today)
    {
        _detail = detail;
        bool requestable = detail.Request.Requestable;
        var choosable = requestable ? detail.Seasons.Where(TitleText.Requestable).ToList() : [];
        _defaults = TitleText.DefaultSeasons(choosable, today);
        _latest = requestable ? TitleText.LatestSeason(detail.Seasons, today) : null;
        _upcoming = TitleText.UpcomingSeasons(choosable, today);
        Seasons = [.. detail.Seasons.Select(s => choosable.Contains(s)
            ? new RequestSeasonChoice(s, isChoosable: true, isPicked: _defaults.Contains(s.SeasonNumber), today)
            : new RequestSeasonChoice(s, isChoosable: false, isPicked: s.Requested, today))];
        foreach (var season in Seasons.Where(s => s.IsChoosable))
        {
            season.PropertyChanged += OnSeasonChanged;
        }
        Description = requestable ? $"Choose the seasons of {detail.Title} to request." : TitleText.UnavailableReason(detail.Request.Reason);
        bool inLibrary = detail.Availability == "available";
        // A series in the library with only upcoming seasons left needs them picked; one outside it is requested whole.
        Hint = _defaults.Count == 0 && choosable.Count > 0
            ? inLibrary ? "Pick the upcoming seasons to request." : "No season has aired yet, so the request covers the whole series unless you pick seasons."
            : "";
        NoSeasonsText = requestable && detail.Seasons.Count == 0
            ? "TMDB lists no seasons for this series yet; the request covers the whole series."
            : "";
        Refresh();
    }

    public string Description { get; }

    public IReadOnlyList<RequestSeasonChoice> Seasons { get; }

    public bool HasSeasons => Seasons.Count > 0;

    /// <summary>Shown while the viewer has not picked: what the request covers then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint))]
    public partial string Hint { get; private set; }

    public bool HasHint => Hint.Length > 0;

    public string NoSeasonsText { get; }

    public bool HasNoSeasonsText => NoSeasonsText.Length > 0;

    private int ChoosableCount => Seasons.Count(s => s.IsChoosable);

    /// <summary>Latest season, offered with more than one season to pick from.</summary>
    public bool CanPickLatest => ChoosableCount > 1 && _latest is not null;

    public bool CanPickUpcoming => ChoosableCount > 1 && _upcoming.Count > 0;

    public bool ShowsAll => ChoosableCount > 1;

    /// <summary>"All seasons": on when every season that can be picked is.</summary>
    [ObservableProperty]
    public partial bool AllPicked { get; set; }

    /// <summary>"Request Seasons 1–3", "Request series" (the server picks), or "Request".</summary>
    [ObservableProperty]
    public partial string SubmitLabel { get; private set; } = "Request";

    [ObservableProperty]
    public partial bool CanSubmit { get; private set; }

    /// <summary>The seasons to send; null leaves them to the server.</summary>
    public IReadOnlyList<int>? PickedSeasons => ServerChooses ? null : Picked();

    // Untouched, the request names no seasons and the server decides; that needs something for it to pick.
    private bool ServerChooses => !_touched && (_detail.Seasons.Count == 0 || _defaults.Count > 0 || _detail.Availability != "available");

    [RelayCommand]
    private void PickLatest()
    {
        if (_latest is { } latest)
        {
            Pick(s => s.SeasonNumber == latest);
        }
    }

    [RelayCommand]
    private void PickUpcoming() => Pick(s => _upcoming.Contains(s.SeasonNumber));

    partial void OnAllPickedChanged(bool value)
    {
        if (!_setting)
        {
            Pick(_ => value);
        }
    }

    private void Pick(Func<RequestSeasonChoice, bool> picked)
    {
        _setting = true;
        try
        {
            foreach (var season in Seasons.Where(s => s.IsChoosable))
            {
                season.IsPicked = picked(season);
            }
        }
        finally
        {
            _setting = false;
        }
        _touched = true;
        Refresh();
    }

    private void OnSeasonChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_setting && e.PropertyName == nameof(RequestSeasonChoice.IsPicked))
        {
            _touched = true;
            Refresh();
        }
    }

    private List<int> Picked() => [.. Seasons.Where(s => s.IsChoosable && s.IsPicked).Select(s => s.SeasonNumber).Order()];

    private void Refresh()
    {
        var picked = Picked();
        _setting = true;
        try
        {
            AllPicked = ChoosableCount > 0 && picked.Count == ChoosableCount;
        }
        finally
        {
            _setting = false;
        }
        if (_touched)
        {
            Hint = "";
        }
        bool serverChooses = ServerChooses;
        SubmitLabel = picked.Count > 0 ? $"Request {RequestText.SeasonList(picked)}" : serverChooses ? "Request series" : "Request";
        CanSubmit = _detail.Request.Requestable && (serverChooses || picked.Count > 0);
        OnPropertyChanged(nameof(PickedSeasons));
    }
}
