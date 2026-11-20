using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>Another preset, offered when the week is empty.</summary>
/// <param name="Command">Takes <see cref="Preset"/>.</param>
public sealed record CalendarPresetLink(string Label, SettingChoice Preset, ICommand Command);

/// <summary>
/// The Calendar: a week of airings (<c>GET /api/v2/calendar</c>, Monday to Sunday in the viewer's zone) as Granary's own
/// week grid, a column per day, which the user chose over the web's rows (<c>pages/Calendar.tsx</c>). The presets
/// Following, Trending and All, the library filter when there are several, the week's arrows and Today, and the texts are
/// the web's. A summary under the title counts the week's episodes, premieres and finales. An airing opens its movie or
/// series. A week with airings shows its empty days too ("Nothing scheduled"); an empty week offers the other presets.
/// </summary>
public sealed partial class CalendarViewModel : ObservableObject
{
    // The web's presets and library filter, worded when shown (pages/Calendar.tsx).
    private static SettingChoice Following => new(CalendarFilters.Following, AppText.Get("web.calendar.presetFollowing"));
    private static SettingChoice Trending => new(CalendarFilters.Trending, AppText.Get("web.calendar.presetTrending"));
    private static SettingChoice Everything => new(CalendarFilters.Everything, AppText.Get("web.calendar.presetAll"));
    private static SettingChoice AllLibraries => new("all", AppText.Get("web.calendar.allLibraries"));

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
    }

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

    /// <summary>The week's seven days, Monday first, once it has any airing; empty while loading, failed or empty.</summary>
    public ObservableCollection<CalendarWeekDay> Days { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsEmpty), nameof(HasError), nameof(IsReady))]
    public partial LibraryLoadState State { get; private set; }

    /// <summary>"9 episodes this week · 2 premieres · 1 finale"; empty while the week has no airings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    public partial string Summary { get; private set; } = "";

    /// <summary>"Nothing upcoming from shows you follow this week.", by preset.</summary>
    [ObservableProperty]
    public partial string EmptyText { get; private set; } = "";

    /// <summary>The other presets, offered when the week is empty.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<CalendarPresetLink> EmptyLinks { get; private set; } = [];

    public bool HasLibraryChoice => LibraryChoices.Count > 0;

    /// <summary>"Oct 5 – 11, 2026".</summary>
    public string WeekLabel => CalendarText.WeekRange(WeekStart);

    public string WeekName => AppText.Get("web.calendar.weekOf", ("range", WeekLabel));

    public bool HasSummary => Summary.Length > 0;

    public bool IsReady => State == LibraryLoadState.Ready;

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
            MoveTodayOn();
            return Task.CompletedTask;
        }
        if (!isReturn)
        {
            WeekStart = CalendarText.WeekStart(Today);
            SetQuietly(() => SelectedLibrary = AllLibraries);
        }
        return LoadAsync();
    }

    // A week kept from an earlier visit: past, today and future go by the date now, so a return after midnight moves TODAY on.
    private void MoveTodayOn()
    {
        var today = Today;
        for (int i = 0; i < Days.Count; i++)
        {
            var day = Days[i];
            var when = day.Date < today ? CalendarDayTime.Past : day.Date == today ? CalendarDayTime.Today : CalendarDayTime.Future;
            if (when != day.When)
            {
                Days[i] = day with { When = when };
            }
        }
    }

    [RelayCommand]
    private Task PreviousWeekAsync() => ShowWeekAsync(WeekStart.AddDays(-7));

    [RelayCommand]
    private Task NextWeekAsync() => ShowWeekAsync(WeekStart.AddDays(7));

    [RelayCommand]
    private Task ThisWeekAsync() => ShowWeekAsync(CalendarText.WeekStart(Today));

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
    private void Open(CalendarAiring? airing)
    {
        if (airing is not null && _opens.TryGetValue(airing.Id, out string? id))
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
        return LoadAsync();
    }

    // The sidebar's libraries. A library that went away (or the profile hid) falls back to all of them; a sidebar still
    // loading changes nothing.
    private void ShowLibraries()
    {
        var libraries = _sidebar.Libraries;
        if (!_sidebar.AreLibrariesLoaded)
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
        Days.Clear();
        _opens.Clear();
        Summary = "";
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
            foreach (var day in DaysOf(week, server.BaseUri))
            {
                Days.Add(day);
            }
            Summary = SummaryOf(week);
            EmptyText = SelectedPreset.Value switch
            {
                CalendarFilters.Following => AppText.Get("web.calendar.emptyFollowing"),
                CalendarFilters.Trending => AppText.Get("web.calendar.emptyTrending"),
                _ => AppText.Get("web.calendar.emptyEverything"),
            };
            EmptyLinks = [.. Presets.Where(p => p != SelectedPreset)
                .Select(p => new CalendarPresetLink(p == Everything ? AppText.Get("web.calendar.showEverything") : p.Label, p, ShowPresetCommand))];
            State = Days.Count == 0 ? LibraryLoadState.Empty : LibraryLoadState.Ready;
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

    // A column for every day of the week once any day has airings, the empty ones too. A day the server names twice is one
    // column; a day outside the week stays out, as the grid has no place for it.
    private List<CalendarWeekDay> DaysOf(CalendarWeek week, Uri server)
    {
        var zone = _time.LocalTimeZone;
        var today = Today;
        var aired = new Dictionary<DateOnly, List<CalendarAiring>>();
        foreach (var (date, airing) in InWeek(week))
        {
            if (!aired.TryGetValue(date, out var airings))
            {
                airings = [];
                aired.Add(date, airings);
            }
            // A movie opens itself; an airing of a series opens the series.
            if (_opens.TryAdd(airing.ContentId, airing.Type == "movie" ? airing.ContentId : airing.SeriesId ?? airing.ContentId))
            {
                airings.Add(AiringOf(airing, server, zone));
            }
        }
        if (aired.Values.All(airings => airings.Count == 0))
        {
            return [];
        }
        return [.. Enumerable.Range(0, 7).Select(i => WeekStart.AddDays(i)).Select(day => new CalendarWeekDay(day,
            day < today ? CalendarDayTime.Past : day == today ? CalendarDayTime.Today : CalendarDayTime.Future,
            aired.GetValueOrDefault(day) ?? []))];
    }

    private CalendarAiring AiringOf(CalendarEvent airing, Uri server, TimeZoneInfo zone)
    {
        string subtitle = CalendarText.Subtitle(airing);
        string code = CalendarText.Code(airing);
        string? time = CalendarText.AirTime(airing, zone);
        string? badge = CalendarText.Badge(airing.Badges);
        return new CalendarAiring(airing.ContentId, airing.Title, OpenCommand)
        {
            Code = code,
            Time = time,
            Badge = badge,
            BadgeKind = CalendarText.BadgeKind(airing.Badges),
            IsWatched = airing.Watched,
            ImageUrl = ArtworkUrl.Resolve(server, airing.PosterUrl),
            ImageThumbhash = airing.PosterThumbhash,
            ToolTip = subtitle != code ? subtitle : null,
            // The title, the episode in the web's capitals, then what the card shows over its picture: the badge, the time and
            // whether it is watched, which its parts do not say themselves.
            AccessibleName = string.Join(", ", new[]
            {
                airing.Title, subtitle.Length > 0 ? AppText.Upper(subtitle) : "", badge, time,
                airing.Watched ? AppText.Get("web.item.card.watched") : "",
            }.Where(part => !string.IsNullOrEmpty(part))),
        };
    }

    // The airings of the week shown with the day they are listed under.
    private IEnumerable<(DateOnly Date, CalendarEvent Airing)> InWeek(CalendarWeek week)
    {
        var weekEnd = WeekStart.AddDays(6);
        foreach (var day in week.Events)
        {
            if (!DateOnly.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || date < WeekStart || date > weekEnd)
            {
                continue;
            }
            foreach (var airing in day.Items.Where(a => !string.IsNullOrWhiteSpace(a.ContentId)))
            {
                yield return (date, airing);
            }
        }
    }

    // "9 episodes this week · 2 premieres · 1 finale": episodes first, else movies. Premieres count only a series' first
    // episodes, as only their cards say PREMIERE.
    private string SummaryOf(CalendarWeek week)
    {
        var airings = InWeek(week).Select(a => a.Airing).DistinctBy(a => a.ContentId, StringComparer.Ordinal).ToList();
        int movies = airings.Count(a => a.Type == "movie");
        int episodes = airings.Count - movies;
        int premieres = airings.Count(a => a.Badges.Contains("series_premiere"));
        int finales = airings.Count(a => a.Badges.Contains("finale"));
        var parts = new List<string>();
        if (episodes > 0)
        {
            parts.Add(AppText.Get("granary.calendar.summaryEpisodes", ("count", episodes)));
            if (movies > 0)
            {
                parts.Add(AppText.Get("granary.calendar.summaryMovieCount", ("count", movies)));
            }
        }
        else if (movies > 0)
        {
            parts.Add(AppText.Get("granary.calendar.summaryMovies", ("count", movies)));
        }
        if (premieres > 0)
        {
            parts.Add(AppText.Get("granary.calendar.summaryPremieres", ("count", premieres)));
        }
        if (finales > 0)
        {
            parts.Add(AppText.Get("granary.calendar.summaryFinales", ("count", finales)));
        }
        return string.Join(" · ", parts);
    }

    private object Key() => (_session.ActiveServer?.Id, _session.ProfileId, WeekStart, SelectedPreset.Value, SelectedLibrary.Value);
}
