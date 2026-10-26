using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// A day of the week strip: "Mon", "5", and a dot when it has airings. Picking a day only changes <see cref="IsSelected"/>,
/// so the strip's buttons stay and keep the keyboard focus.
/// </summary>
/// <param name="selectCommand">Takes the chip.</param>
public sealed partial class CalendarDayChip(DateOnly date, string label, string day, bool isToday, bool hasEvents, ICommand selectCommand) : ObservableObject
{
    public DateOnly Date { get; } = date;

    public string Label { get; } = label;

    public string Day { get; } = day;

    public bool IsToday { get; } = isToday;

    public bool HasEvents { get; } = hasEvents;

    public ICommand SelectCommand { get; } = selectCommand;

    [ObservableProperty]
    public partial bool IsSelected { get; internal set; }

    public string AccessibleName => $"{Label} {Day}{(HasEvents ? " (has events)" : "")}";
}

/// <summary>Another preset, offered when the week is empty.</summary>
/// <param name="Command">Takes <see cref="Preset"/>.</param>
public sealed record CalendarPresetLink(string Label, SettingChoice Preset, ICommand Command);

/// <summary>A day's airings under "Monday, October 5th", or none; today's says TODAY.</summary>
/// <param name="OpenCommand">Takes the airing's <see cref="MediaCard"/>.</param>
public sealed record CalendarDayRow(DateOnly Date, string Heading, bool IsToday, IReadOnlyList<MediaCard> Cards, ICommand OpenCommand)
{
    public string Badge => IsToday ? "TODAY" : "";

    public bool HasBadge => IsToday;

    public bool HasAirings => Cards.Count > 0;

    public bool HasNoAirings => Cards.Count == 0;
}

/// <summary>
/// The Calendar, as Silo's web shows it (<c>pages/Calendar.tsx</c>): a week of airings (<c>GET /api/v2/calendar</c>,
/// Monday to Sunday in the viewer's zone) as a row per day, the week strip with a dot on days that have some, the
/// presets Following, Trending and All, and a library filter when there are several. A day in the strip scrolls to its
/// row. An airing opens its movie or series. Unlike the web, a week with airings lists its empty days too ("Nothing
/// scheduled."), so a day picked in the strip always has its own place to scroll to.
/// </summary>
public sealed partial class CalendarViewModel : ObservableObject
{
    /// <summary>The <see cref="MediaCard.Type"/> of an airing: no card actions, the episode may not be in a library yet.</summary>
    public const string CardType = "calendar";

    private static readonly SettingChoice Following = new(CalendarFilters.Following, "Following");
    private static readonly SettingChoice Trending = new(CalendarFilters.Trending, "Trending");
    private static readonly SettingChoice Everything = new(CalendarFilters.Everything, "All");
    private static readonly SettingChoice AllLibraries = new("all", "All Libraries");

    private readonly ISessionManager _session;
    private readonly CalendarApi _calendar;
    private readonly SidebarViewModel _sidebar;
    private readonly INavigationService _navigation;
    private readonly TimeProvider _time;
    private readonly ILogger<CalendarViewModel> _logger;
    private readonly Dictionary<string, string> _opens = new(StringComparer.Ordinal);
    private object? _loadedFor;
    private bool _settingQuietly;
    private CancellationTokenSource _run = new();

    public CalendarViewModel(ISessionManager session, CalendarApi calendar, SidebarViewModel sidebar, INavigationService navigation,
        TimeProvider time, ILogger<CalendarViewModel> logger)
    {
        _session = session;
        _calendar = calendar;
        _sidebar = sidebar;
        _navigation = navigation;
        _time = time;
        _logger = logger;
        WeekStart = CalendarText.WeekStart(Today);
        Days = ChipsOf([]);
    }

    /// <summary>Raised with the day whose row the page should scroll to.</summary>
    public event EventHandler<DateOnly>? DayRequested;

    public IReadOnlyList<SettingChoice> Presets { get; } = [Following, Trending, Everything];

    [ObservableProperty]
    public partial SettingChoice SelectedPreset { get; set; } = Following;

    /// <summary>All Libraries, then each library; empty with one library or none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLibraryChoice))]
    public partial IReadOnlyList<SettingChoice> LibraryChoices { get; private set; } = [];

    [ObservableProperty]
    public partial SettingChoice SelectedLibrary { get; set; } = AllLibraries;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WeekLabel), nameof(WeekName))]
    public partial DateOnly WeekStart { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<CalendarDayChip> Days { get; private set; }

    public ObservableCollection<CalendarDayRow> Rows { get; } = [];

    /// <summary>The day picked in the strip, while it is in the week shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDayEmptyText), nameof(HasSelectedDayEmpty))]
    public partial DateOnly? SelectedDay { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsEmpty), nameof(HasError), nameof(SelectedDayEmptyText), nameof(HasSelectedDayEmpty))]
    public partial LibraryLoadState State { get; private set; }

    /// <summary>"Nothing upcoming from shows you follow this week.", by preset.</summary>
    [ObservableProperty]
    public partial string EmptyText { get; private set; } = "";

    /// <summary>The other presets, offered when the week is empty.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<CalendarPresetLink> EmptyLinks { get; private set; } = [];

    public bool HasLibraryChoice => LibraryChoices.Count > 0;

    /// <summary>"Oct 5 – 11, 2026".</summary>
    public string WeekLabel => CalendarText.WeekRange(WeekStart);

    public string WeekName => $"Week of {WeekLabel}";

    /// <summary>"Nothing scheduled for Monday, October 5th." when a day is picked in a week without any airings.</summary>
    public string SelectedDayEmptyText => HasSelectedDayEmpty ? $"Nothing scheduled for {CalendarText.DayHeading(SelectedDay!.Value)}." : "";

    // A week with airings has a row for every day, which says so itself.
    public bool HasSelectedDayEmpty => SelectedDay is not null && State == LibraryLoadState.Empty;

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool IsEmpty => State == LibraryLoadState.Empty;

    public bool HasError => State == LibraryLoadState.Error;

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone).DateTime);

    /// <summary>
    /// Shows the page. Back keeps the week shown for the same profile. A visit from the sidebar opens this week of every
    /// library, as the web's link does; the preset stays.
    /// </summary>
    public Task ActivateAsync(bool isReturn)
    {
        ShowLibraries();
        bool shown = Equals(_loadedFor, Key()) && State is LibraryLoadState.Ready or LibraryLoadState.Empty or LibraryLoadState.Loading;
        if (isReturn && shown)
        {
            return Task.CompletedTask;
        }
        if (!isReturn)
        {
            WeekStart = CalendarText.WeekStart(Today);
            SelectedDay = null;
            SetQuietly(() => SelectedLibrary = AllLibraries);
        }
        return LoadAsync();
    }

    [RelayCommand]
    private Task PreviousWeekAsync() => ShowWeekAsync(WeekStart.AddDays(-7));

    [RelayCommand]
    private Task NextWeekAsync() => ShowWeekAsync(WeekStart.AddDays(7));

    [RelayCommand]
    private Task ThisWeekAsync() => ShowWeekAsync(CalendarText.WeekStart(Today));

    [RelayCommand]
    private void SelectDay(CalendarDayChip? chip)
    {
        if (chip is null)
        {
            return;
        }
        SelectedDay = chip.Date;
        foreach (var day in Days)
        {
            day.IsSelected = day.Date == chip.Date;
        }
        DayRequested?.Invoke(this, chip.Date);
    }

    /// <summary>One of <see cref="EmptyLinks"/>.</summary>
    [RelayCommand]
    private void ShowPreset(SettingChoice? preset)
    {
        if (preset is not null)
        {
            SelectedPreset = preset;
        }
    }

    [RelayCommand]
    private Task RetryAsync() => LoadAsync();

    [RelayCommand]
    private void Open(MediaCard? card)
    {
        if (card is not null && _opens.TryGetValue(card.Id, out string? id))
        {
            _navigation.NavigateTo(AppRoute.Item, id);
        }
    }

    partial void OnSelectedPresetChanged(SettingChoice value) => Reload();

    partial void OnSelectedLibraryChanged(SettingChoice value) => Reload();

    // A change the viewer made loads the week again; one the page makes itself leaves the loading to it.
    private void Reload()
    {
        if (!_settingQuietly)
        {
            _ = LoadAsync();
        }
    }

    private void SetQuietly(Action change)
    {
        _settingQuietly = true;
        try
        {
            change();
        }
        finally
        {
            _settingQuietly = false;
        }
    }

    private Task ShowWeekAsync(DateOnly start)
    {
        if (start == WeekStart && State != LibraryLoadState.Error)
        {
            return Task.CompletedTask;
        }
        WeekStart = start;
        SelectedDay = null;
        return LoadAsync();
    }

    // The sidebar's libraries. A library that went away falls back to all of them; a sidebar still loading changes nothing.
    private void ShowLibraries()
    {
        var libraries = _sidebar.Libraries;
        if (libraries.Count == 0)
        {
            return;
        }
        LibraryChoices = libraries.Count > 1 ? [AllLibraries, .. libraries.Select(l => new SettingChoice(l.Id, l.Name))] : [];
        if (SelectedLibrary != AllLibraries && !LibraryChoices.Contains(SelectedLibrary))
        {
            SetQuietly(() => SelectedLibrary = AllLibraries);
        }
    }

    private async Task LoadAsync()
    {
        var previous = _run;
        _run = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        var token = _run.Token;
        Rows.Clear();
        _opens.Clear();
        Days = ChipsOf([]);
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            _loadedFor = null;
            State = LibraryLoadState.Idle;
            return;
        }
        _loadedFor = Key();
        State = LibraryLoadState.Loading;
        try
        {
            string? library = SelectedLibrary == AllLibraries ? null : SelectedLibrary.Value;
            var week = await _calendar.GetAsync(server.BaseUri, WeekStart, WeekStart.AddDays(6), SelectedPreset.Value,
                CalendarText.IanaZone(_time.LocalTimeZone, _time.GetUtcNow()), library, token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            foreach (var row in RowsOf(week, server.BaseUri))
            {
                Rows.Add(row);
            }
            Days = ChipsOf(Rows);
            EmptyText = SelectedPreset.Value switch
            {
                CalendarFilters.Following => "Nothing upcoming from shows you follow this week.",
                CalendarFilters.Trending => "Nothing trending this week.",
                _ => "Nothing scheduled this week.",
            };
            EmptyLinks = [.. Presets.Where(p => p != SelectedPreset)
                .Select(p => new CalendarPresetLink(p == Everything ? "Show everything" : p.Label, p, ShowPresetCommand))];
            State = Rows.Count == 0 ? LibraryLoadState.Empty : LibraryLoadState.Ready;
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested)
            {
                // Another week, preset or library took over.
                return;
            }
            if (LibraryErrors.IsExpected(ex))
            {
                _logger.LogWarning(ex, "Could not load the calendar");
            }
            else
            {
                // Still the page's to show: a spinner that never ends would hide it.
                _logger.LogError(ex, "The calendar could not be shown");
            }
            State = LibraryLoadState.Error;
        }
    }

    private List<CalendarDayRow> RowsOf(CalendarWeek week, Uri server)
    {
        var rows = new List<CalendarDayRow>();
        var zone = _time.LocalTimeZone;
        var today = Today;
        foreach (var day in week.Events)
        {
            if (!DateOnly.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                continue;
            }
            var cards = new List<MediaCard>();
            foreach (var airing in day.Items.Where(a => !string.IsNullOrWhiteSpace(a.ContentId)))
            {
                // A movie opens itself; an airing of a series opens the series.
                if (!_opens.TryAdd(airing.ContentId, airing.Type == "movie" ? airing.ContentId : airing.SeriesId ?? airing.ContentId))
                {
                    continue;
                }
                string subtitle = CalendarText.Subtitle(airing);
                cards.Add(new MediaCard(airing.ContentId, CardType, airing.Title)
                {
                    Shape = MediaCardShape.Poster,
                    Subtitle = subtitle.Length > 0 ? subtitle.ToUpperInvariant() : null,
                    Caption = CalendarText.AirTime(airing, zone),
                    ImageUrl = ArtworkUrl.Resolve(server, airing.PosterUrl),
                    StatusBadge = CalendarText.Badge(airing.Badges),
                    IsWatched = airing.Watched,
                    ShowsWatchedMark = airing.Watched,
                });
            }
            if (cards.Count > 0)
            {
                rows.Add(new CalendarDayRow(date, CalendarText.DayHeading(date), date == today, cards, OpenCommand));
            }
        }
        if (rows.Count == 0)
        {
            return rows;
        }
        // Every day of the week, in order, the empty ones too.
        var aired = rows.ToDictionary(r => r.Date);
        var days = Enumerable.Range(0, 7).Select(i => WeekStart.AddDays(i))
            .Select(day => aired.GetValueOrDefault(day) ?? new CalendarDayRow(day, CalendarText.DayHeading(day), day == today, [], OpenCommand));
        return [.. days.Concat(rows.Where(r => r.Date < WeekStart || r.Date > WeekStart.AddDays(6))).OrderBy(r => r.Date)];
    }

    private List<CalendarDayChip> ChipsOf(IEnumerable<CalendarDayRow> rows)
    {
        var withAirings = rows.Where(r => r.HasAirings).Select(r => r.Date).ToHashSet();
        var today = Today;
        return [.. Enumerable.Range(0, 7).Select(i => WeekStart.AddDays(i)).Select(day => new CalendarDayChip(day, CalendarText.ShortDay(day),
            day.Day.ToString(CultureInfo.InvariantCulture), day == today, withAirings.Contains(day), SelectDayCommand) { IsSelected = day == SelectedDay })];
    }

    private object Key() => (_session.ActiveServer?.Id, _session.ProfileId, WeekStart, SelectedPreset.Value, SelectedLibrary.Value);
}
