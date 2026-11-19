using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>A file the download dialog lists: its quality line and size, as the web's dialog lists a title's versions.</summary>
public sealed record DownloadVersionRow(string FileId, string Summary, long FileSize)
{
    /// <summary>"38.9 GB"; empty when the server gave no size.</summary>
    public string SizeText => PrePlayText.FileSize(FileSize);
}

/// <summary>A preset the server offers this profile, with the apps' label: "Original", "10 Mbps · up to 1080p".</summary>
public sealed record DownloadQualityRow(string Preset, string Label);

/// <summary>
/// The download dialog of a film, an episode, a season or a whole series. Its title, texts and version rows are the web's
/// (<c>web/src/components/DownloadVersionPicker.tsx</c>); the qualities are the ones the server offers, as the apps list them
/// (downloads-api.md, 10.3), and the free space line keeps a download that cannot fit from starting. The free space is read
/// off the UI thread once per dialog (<see cref="SpaceRead"/>) and shown on it; a read that fails or takes longer than
/// <see cref="SpaceReadTimeout"/> leaves it unknown, which stops nothing. Made per offer: <see cref="ForTitle"/> or
/// <see cref="ForSeason"/> from a title page, which knows the files, or <see cref="ForReading"/> from a card, whose dialog
/// opens at once and reads them while it shows (<see cref="Reading"/>), as the web's card menu reads an item for its dialogs
/// (<c>MediaItemMenu.tsx</c>, <c>MetadataActionDialogHost</c>); then the dialog, whose Download calls <see cref="StartAsync"/>.
/// </summary>
public sealed partial class DownloadOptionsViewModel : ObservableObject
{
    /// <summary>The web's description under the title (<c>web.downloads.versionPicker.description</c>).</summary>
    public static string Description => AppText.Get("web.downloads.versionPicker.description");

    /// <summary>The web's line under more than one file (<c>web.downloads.versionPicker.sizeNote</c>).</summary>
    public static string Footer => AppText.Get("web.downloads.versionPicker.sizeNote");

    /// <summary>The web's words when a download did not start (<c>web.downloads.versionPicker.startFailed</c>).</summary>
    public static string StartFailed => AppText.Get("web.downloads.versionPicker.startFailed");

    /// <summary>
    /// The server refused a film or an episode for the account's limit of downloads (429 <c>rate_limited</c>): Granary's words,
    /// which name that limit.
    /// </summary>
    public static string LimitReached => AppText.Get("granary.downloadOptions.limitReached");

    /// <summary>
    /// A season or a series past the server's limit of downloads: "Your Silo server's download limit is reached. 6 episodes wait
    /// on this PC and start once the server allows more."
    /// </summary>
    public static string WaitingText(int count) => AppText.Get("granary.downloads.waitingForLimit", ("count", count));

    /// <summary>The web's words while a card's dialog reads its item (<c>web.item.menu.loadingDetails</c>).</summary>
    public static string LoadingDetails => AppText.Get("web.item.menu.loadingDetails");

    /// <summary>The web's words when that read failed (<c>web.item.menu.detailsFailed</c>).</summary>
    public static string DetailsFailed => AppText.Get("web.item.menu.detailsFailed");

    // The dialog's title before it knows what it offers.
    private static string PlainTitle => AppText.Get("web.downloads.versionPicker.title");

    /// <summary>
    /// The longest the free space is waited for (<see cref="DiskSpaceReads.Timeout"/>): a share that does not answer would
    /// otherwise keep Download doing nothing for half a minute.
    /// </summary>
    public static readonly TimeSpan SpaceReadTimeout = DiskSpaceReads.Timeout;

    // A preset's file is its video bitrate over the runtime and a tenth more for audio and the container.
    private const double EstimateMargin = 1.1;

    private readonly DownloadManager _downloads;
    private readonly DownloadsFeature _feature;
    private readonly IDownloadPreferences _preferences;
    private readonly IDiskSpace _disk;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<DownloadOptionsViewModel> _logger;
    private readonly SeriesMonitors? _monitors;
    private readonly MonitorSchedule? _schedule;

    // What the dialog offers, as given or read; the choices the viewer makes in it pick from it what Download starts.
    private DownloadOffer? _offer;

    // The profile active as the dialog opened, or as its last read of the offer began: Download starts for it, and sends
    // nothing once another one is active.
    private DownloadScope? _openedFor;

    private DownloadRequest? _title;
    private SeasonDownloadRequest? _season;
    private SeriesDownloadRequest? _series;

    // What a season's or a series' episodes take on the server, as their offer counted it.
    private long _episodesBytes;
    private int _runtimeSeconds;
    private Dictionary<string, DownloadQualityOption> _options = new(StringComparer.Ordinal);

    // The bytes the selection needs, 0 when not known; the free bytes once read, null until then or when the drive is not there.
    private long _needed;
    private long? _free;

    // What the downloads on this PC take, as the dialog opened and again as Download is pressed: the storage limit for downloads
    // counts it, and downloads go on while the dialog shows.
    private long _taken;
    private int _reads;

    // A card's read of what to offer, and the one under way (Try Again starts another, the dialog's close stops it).
    private Func<CancellationToken, Task<DownloadOffer>>? _read;
    private CancellationTokenSource? _reading;
    private int _offerReads;

    // The run Download started, which the viewer's Cancel stops (StopStarting); null while nothing starts.
    private CancellationTokenSource? _stopping;

    public DownloadOptionsViewModel(DownloadManager downloads, DownloadsFeature feature, IDownloadPreferences preferences, IDiskSpace disk,
        IUiDispatcher dispatcher, TimeProvider time, ILogger<DownloadOptionsViewModel> logger, SeriesMonitors? monitors = null,
        MonitorSchedule? schedule = null)
    {
        _downloads = downloads;
        _feature = feature;
        _preferences = preferences;
        _disk = disk;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
        _monitors = monitors;
        _schedule = schedule;
    }

    /// <summary>
    /// "Download: Example Movie", "Download: Example Series · S1 · E5", "Download: Example Series · Season 1", "Download: Example
    /// Series · All episodes"; "Download" while a card's dialog does not know its title yet.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DialogTitle))]
    public partial string Title { get; private set; } = PlainTitle;

    /// <summary>The title's files, the highest resolution first; none for a season or a series.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsVersions), nameof(ShowsFooter))]
    public partial IReadOnlyList<DownloadVersionRow> Versions { get; private set; } = [];

    /// <summary>
    /// There is something to download: a title with a file, or a season's or a series' episodes. A card's dialog has it once it
    /// read its title, and only when there is.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsVersions), nameof(ShowsFooter), nameof(ShowsQualities), nameof(CanStart))]
    public partial bool HasOffer { get; private set; }

    /// <summary>Only a title with more than one file has version rows, as on the web.</summary>
    public bool ShowsVersions => HasOffer && Versions.Count > 1;

    /// <summary>The web's footer goes with its version rows.</summary>
    public bool ShowsFooter => ShowsVersions;

    public IReadOnlyList<DownloadQualityRow> Qualities { get; private set; } = [];

    /// <summary>A choice of quality shows only when the server offers more than Original, and there is something to download.</summary>
    public bool ShowsQualities => HasOffer && Qualities.Count > 1;

    /// <summary>
    /// What the dialog says under its title: the web's <see cref="Description"/>; while a card's dialog reads its title
    /// <see cref="LoadingDetails"/>, and <see cref="DetailsFailed"/> when it could not; why nothing can be downloaded when nothing can.
    /// </summary>
    [ObservableProperty]
    public partial string DescriptionText { get; private set; } = Description;

    /// <summary>A card's dialog reads its title's files: the web's "Loading…" shows.</summary>
    [ObservableProperty]
    public partial bool IsReading { get; private set; }

    /// <summary>That read failed: the web's "Please try again." and Try Again show.</summary>
    [ObservableProperty]
    public partial bool ReadFailed { get; private set; }

    /// <summary>The read a card's dialog started last (Try Again starts another); done once the dialog shows what it read.</summary>
    public Task Reading { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    public partial DownloadVersionRow? SelectedVersion { get; set; }

    [ObservableProperty]
    public partial DownloadQualityRow? SelectedQuality { get; set; }

    /// <summary>"8.0 GB" for the original, "About 2.1 GB" for a preset, "10 episodes · 21.4 GB" for a season or a series.</summary>
    [ObservableProperty]
    public partial string SizeText { get; private set; } = "";

    /// <summary>"200.0 GB free on this PC.", or why the selection does not fit; empty until the space is read.</summary>
    [ObservableProperty]
    public partial string SpaceText { get; private set; } = "";

    /// <summary>The selection needs more than the drive has free, keeping the transfer's reserve.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    public partial bool IsShortOfSpace { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    public partial bool IsStarting { get; private set; }

    /// <summary>
    /// What is chosen starts something: a film or an episode, or at least one episode of a season or a series. "Only unwatched
    /// episodes" can leave none.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    public partial bool HasSelection { get; private set; }

    /// <summary>
    /// Download can be pressed: there is something to download, nothing is starting and the selection fits, as far as the free
    /// space is known yet.
    /// </summary>
    public bool CanStart => IsMonitorChosen ? CanSaveMonitor : HasOffer && HasSelection && !IsStarting && !IsShortOfSpace;

    /// <summary>Why the last press of Download did not start it; empty when it did, or before.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorText { get; private set; } = "";

    public bool HasError => ErrorText.Length > 0;

    /// <summary>
    /// What the last press of Download left waiting, as information rather than an error (<see cref="WaitingText"/>); empty when
    /// nothing waits, or before.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice), nameof(CloseText))]
    public partial string NoticeText { get; private set; } = "";

    public bool HasNotice => NoticeText.Length > 0;

    /// <summary>The dialog's other button: the web's Cancel, or Done once a press left episodes waiting, which stay as they are.</summary>
    public string CloseText => HasNotice ? AppText.Get("common.done") : AppText.Get("common.cancel");

    /// <summary>The read of the free space this dialog started; done once <see cref="SpaceText"/> shows it.</summary>
    public Task SpaceRead { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// A film or an episode: <paramref name="request"/> with the version picked on the title page (its
    /// <see cref="DownloadRequest.MediaFileId"/>, else the first row), and the title's files. The runtime sizes a preset.
    /// </summary>
    public void ForTitle(DownloadRequest request, IReadOnlyList<DownloadVersionRow> versions, int runtimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(versions);
        Open(Take(new TitleDownloadOffer(request, versions, runtimeSeconds)));
    }

    /// <summary>A season's episodes, one download each; <paramref name="knownBytes"/> is what their files take on the server.</summary>
    public void ForSeason(SeasonDownloadRequest request, string seasonTitle, long knownBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        ForSeason(new SeasonDownloadOffer(request, seasonTitle, knownBytes));
    }

    /// <summary>A season's episodes as its page offers them, one download each, with Only unwatched when one of them is watched.</summary>
    public void ForSeason(SeasonDownloadOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        Open(Take(offer));
    }

    /// <summary>
    /// A card's film, episode, season or series, whose files the card does not carry: the dialog opens with "Download" and the web's
    /// loading words while <paramref name="read"/> reads what to offer, then shows it as <see cref="ForTitle"/> or
    /// <see cref="ForSeason"/> would, or why there is nothing to download, or that the read failed, with Try Again. A series' dialog (<paramref name="monitor"/>) offers Monitor beside it while the series
    /// can be monitored, and opens on it when <paramref name="startsOnMonitor"/> or the series is monitored.
    /// </summary>
    public void ForReading(Func<CancellationToken, Task<DownloadOffer>> read, SeriesMonitorTarget? monitor = null, bool startsOnMonitor = false)
    {
        ArgumentNullException.ThrowIfNull(read);
        _read = read;
        _offer = null;
        _title = null;
        _season = null;
        _series = null;
        _episodesBytes = 0;
        _runtimeSeconds = 0;
        Title = PlainTitle;
        Versions = [];
        HasOffer = false;
        HasSelection = false;
        Open(null);
        SetUpMonitor(monitor, startsOnMonitor);
        Reading = ReadOfferAsync();
    }

    /// <summary>The dialog closed: a read under way stops, and nothing of it shows.</summary>
    public void StopReading()
    {
        if (_reading is { } reading)
        {
            _reading = null;
            reading.Cancel();
            reading.Dispose();
        }
    }

    /// <summary>The web's Try Again after a read that failed: the title is read again.</summary>
    [RelayCommand]
    private Task TryAgainAsync() => Reading = ReadOfferAsync();

    /// <summary>
    /// Starts what the dialog shows in the selected version and quality, once the free space is known: true when it started
    /// (or was on this PC already) and the dialog closes, false when it does not fit or <see cref="ErrorText"/> says why. A
    /// season or a series whose episodes did not all start is false; pressing again sends only the ones still missing. Also
    /// true once the viewer stopped it (<see cref="StopStarting"/>), whatever the last create came to: the dialog closes with
    /// what started so far, since the viewer chose to leave it.
    /// </summary>
    public async Task<bool> StartAsync()
    {
        if (!IsStarting && IsMonitorChosen)
        {
            // The save sets IsStarting itself before it waits for anything, so the dialog holds Cancel until it answered.
            using var saving = new CancellationTokenSource();
            _stopping = saving;
            try
            {
                return await SaveMonitorAsync(saving.Token);
            }
            finally
            {
                _stopping = null;
            }
        }
        // Nothing chosen starts nothing: a season with every episode left watched and Only unwatched on would otherwise count
        // as started with no create sent.
        if (IsStarting || !HasSelection || (_title is null && _season is null && _series is null))
        {
            return false;
        }
        IsStarting = true;
        ErrorText = "";
        NoticeText = "";
        using var stopping = new CancellationTokenSource();
        _stopping = stopping;
        try
        {
            await SpaceRead;
            await _dispatcher.ResumeOnUi();
            if (stopping.IsCancellationRequested)
            {
                return true;
            }
            // Downloads that came or grew while the dialog showed count against the limit too.
            _taken = _downloads.BytesTaken();
            ShowSpace();
            if (IsShortOfSpace)
            {
                return false;
            }
            (StartOutcome Kind, int Waiting) outcome;
            try
            {
                outcome = await StartSelectionAsync(stopping.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Whatever went wrong, the dialog says it did not start, and Download can be pressed again.
                _logger.LogWarning("A download could not be started ({Failure})", DownloadLog.Failure(ex));
                outcome = (StartOutcome.Failed, 0);
            }
            await _dispatcher.ResumeOnUi();
            if (stopping.IsCancellationRequested)
            {
                return true;
            }
            ErrorText = outcome.Kind switch
            {
                StartOutcome.Failed => StartFailed,
                StartOutcome.Limited => LimitReached,
                _ => "",
            };
            // The dialog stays to say what waits; Done closes it, and they wait all the same.
            NoticeText = outcome.Waiting > 0 ? WaitingText(outcome.Waiting) : "";
            return outcome.Kind == StartOutcome.Started;
        }
        finally
        {
            _stopping = null;
            IsStarting = false;
        }
    }

    /// <summary>
    /// The viewer's Cancel while Download runs: a season's or a series' run stops after the episode under way, whose create
    /// is kept, and <see cref="StartAsync"/> then ends as stopped, so the dialog closes with no download starting after it.
    /// A film's or an episode's single create ends as it would. Nothing while nothing starts.
    /// </summary>
    public void StopStarting() => _stopping?.Cancel();

    // Another choice is another start: the last one's error no longer says anything about it.
    partial void OnSelectedVersionChanged(DownloadVersionRow? value)
    {
        ErrorText = "";
        ShowSize();
    }

    partial void OnSelectedQualityChanged(DownloadQualityRow? value)
    {
        ErrorText = "";
        ShowSize();
    }

    // What the offer downloads as the viewer's choices take it, its title and files; answers the version to choose first, none
    // for a season or a series.
    private DownloadVersionRow? Take(DownloadOffer offer)
    {
        _offer = offer;
        ShowRows(offer);
        return Choose();
    }

    // The offer as the choices take it (DownloadOptionsViewModel.Episodes.cs), shown: its title, files and whether it starts
    // anything. Answers the version to choose first, none for a season or a series.
    private DownloadVersionRow? Choose()
    {
        var offer = Chosen();
        HasSelection = offer switch
        {
            TitleDownloadOffer => ChosenRowIsAvailable(),
            SeasonDownloadOffer season => season.Request.Episodes.Count > 0,
            SeriesDownloadOffer series => series.Request.EpisodeCount > 0,
            _ => false,
        };
        _title = (offer as TitleDownloadOffer)?.Request;
        _season = (offer as SeasonDownloadOffer)?.Request;
        _series = (offer as SeriesDownloadOffer)?.Request;
        _episodesBytes = offer switch
        {
            SeasonDownloadOffer season => Math.Max(0, season.KnownBytes),
            SeriesDownloadOffer series => Math.Max(0, series.KnownBytes),
            _ => 0,
        };
        _runtimeSeconds = offer is TitleDownloadOffer { RuntimeSeconds: var seconds } ? Math.Max(0, seconds) : 0;
        switch (offer)
        {
            case TitleDownloadOffer title:
                Title = TitleWithName(TitleOf(title.Request));
                Versions = title.Versions;
                HasOffer = true;
                return title.Versions.FirstOrDefault(v => v.FileId == title.Request.MediaFileId) ?? title.Versions.FirstOrDefault();
            case SeasonDownloadOffer season:
                Title = TitleWithName($"{season.Request.SeriesTitle} · {SeasonName(season.Request.SeasonNumber, season.SeasonTitle)}");
                Versions = [];
                HasOffer = true;
                return null;
            case SeriesDownloadOffer series:
                Title = TitleWithName($"{series.Request.SeriesTitle} · {AppText.Get("granary.downloadOptions.allEpisodes")}");
                Versions = [];
                HasOffer = true;
                return null;
            default:
                Versions = [];
                HasOffer = false;
                return null;
        }
    }

    // One read at a time, off the UI thread and shown on it; one the dialog closed on, or that Try Again replaced, shows nothing.
    // Its token's source is the UI thread's: whoever stops a read, or the read that ends, lets go of it.
    private async Task ReadOfferAsync()
    {
        if (_read is not { } read)
        {
            return;
        }
        // Each read is for the profile active as it begins, and so is Download: Try Again after a switch of profile reads, and
        // downloads, for the new one.
        _openedFor = _downloads.ActiveScope;
        StopReading();
        var reading = new CancellationTokenSource();
        _reading = reading;
        int attempt = ++_offerReads;
        IsReading = true;
        ReadFailed = false;
        DescriptionText = LoadingDetails;
        DownloadOffer? offer = null;
        try
        {
            offer = await read(reading.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (reading.IsCancellationRequested)
        {
            // The dialog closed, or Try Again read again.
        }
        catch (Exception ex)
        {
            // The dialog says the read failed and offers Try Again, which reads for the profile active then; nothing is thrown
            // into it. Only the failure's kind is logged.
            if (LibraryErrors.IsExpected(ex) || ex is ProfileChangedException)
            {
                _logger.LogWarning("The title's files could not be read for the download dialog ({Failure})", DownloadLog.Failure(ex));
            }
            else
            {
                _logger.LogError("Reading the title's files for the download dialog failed unexpectedly ({Failure})", DownloadLog.Failure(ex));
            }
        }
        await _dispatcher.ResumeOnUi();
        if (attempt != _offerReads || reading.IsCancellationRequested)
        {
            return;
        }
        _reading = null;
        reading.Dispose();
        IsReading = false;
        if (offer is null)
        {
            ReadFailed = true;
            DescriptionText = DetailsFailed;
            return;
        }
        Show(offer);
    }

    // What was read: what it offers, with its first version chosen and its size, or why there is nothing to download. The
    // qualities and the free space stay as the dialog opened with them.
    private void Show(DownloadOffer offer)
    {
        if (offer is NoDownloadOffer nothing)
        {
            Take(offer);
            DescriptionText = nothing.Reason;
            return;
        }
        var version = Take(offer);
        if (offer is SeriesDownloadOffer series)
        {
            // Every season left, whatever Only unwatched leaves out of a one-time download.
            NoteSeasons(series.Request);
        }
        DescriptionText = Description;
        SelectedVersion = version;
        ShowSize();
    }

    // The rows as the dialog opens: the server's qualities with the viewer's default chosen, the size, and a fresh read of the space.
    private void Open(DownloadVersionRow? version)
    {
        _openedFor = _downloads.ActiveScope;
        ErrorText = "";
        var options = OfferedQualities();
        _options = options.ToDictionary(o => o.Preset, StringComparer.Ordinal);
        Qualities = [.. options.Select(o => new DownloadQualityRow(o.Preset, DownloadText.QualityLabel(o)))];
        SelectedVersion = version;
        SelectedQuality = Qualities.FirstOrDefault(q => q.Preset == _preferences.DefaultQuality)
            ?? Qualities.FirstOrDefault(q => q.Preset == DownloadQualities.Original)
            ?? Qualities[0];
        _taken = _downloads.BytesTaken();
        ShowSize();
        ReadSpace();
    }

    // Only what the server lists, in its order (downloads-api.md, 10.3), each with its option for the label; Original when it
    // lists nothing, which every server takes.
    private List<DownloadQualityOption> OfferedQualities()
    {
        var capability = _feature.Capability;
        List<DownloadQualityOption> offered = capability is null
            ? []
            : [.. capability.QualityPresets.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal)
                .Select(p => capability.QualityOptions.FirstOrDefault(o => o.Preset == p) ?? new DownloadQualityOption(p, null, null))];
        return offered.Count > 0 ? offered : [new DownloadQualityOption(DownloadQualities.Original, null, null)];
    }

    private void ShowSize()
    {
        (SizeText, _needed) = _season is { } season ? EpisodesSize(season.Episodes.Count)
            : _series is { } series ? EpisodesSize(series.EpisodeCount)
            : TitleSize();
        ShowSpace();
    }

    // The original's size, or a preset's estimate; never above the source, which the server never encodes above.
    private (string Text, long Bytes) TitleSize()
    {
        long source = Math.Max(0, SelectedVersion?.FileSize ?? 0);
        if (IsOriginal)
        {
            return (PrePlayText.FileSize(source), source);
        }
        long estimate = Estimate();
        if (estimate <= 0)
        {
            return ("", 0);
        }
        estimate = source > 0 ? Math.Min(estimate, source) : estimate;
        return (AppText.Get("granary.downloadOptions.aboutSize", ("size", PrePlayText.FileSize(estimate))), estimate);
    }

    // A season's or a series' episodes and, for the original, what their files take; a preset's files are not known before the
    // server makes them.
    private (string Text, long Bytes) EpisodesSize(int count)
    {
        string episodes = AppText.Get("granary.downloadOptions.episodes", ("count", count));
        return IsOriginal && _episodesBytes > 0 ? ($"{episodes} · {PrePlayText.FileSize(_episodesBytes)}", _episodesBytes) : (episodes, 0);
    }

    private bool IsOriginal => SelectedQuality is null || SelectedQuality.Preset == DownloadQualities.Original;

    private long Estimate()
    {
        if (SelectedQuality is not { } quality || _runtimeSeconds <= 0)
        {
            return 0;
        }
        int? kbps = _options.TryGetValue(quality.Preset, out var option) ? option.BitrateKbps : null;
        kbps ??= quality.Preset.EndsWith("mbps", StringComparison.Ordinal)
            && int.TryParse(quality.Preset[..^4], NumberStyles.None, CultureInfo.InvariantCulture, out int mbps) ? mbps * 1000 : null;
        return kbps is > 0 ? (long)(kbps.Value * 1000d / 8 * _runtimeSeconds * EstimateMargin) : 0;
    }

    // Windows can take its time over a drive that sleeps or a share: the free space is read on the thread pool and shown on the UI thread.
    private void ReadSpace()
    {
        int read = ++_reads;
        _free = null;
        ShowSpace();
        string folder = _preferences.Folder;
        SpaceRead = ReadSpaceAsync(folder, read);
    }

    // Unknown (null) when the read fails or does not answer in time: the space is then not what holds a download back.
    private async Task ReadSpaceAsync(string folder, int read)
    {
        long? free = await _disk.FreeBytesAsync(folder, _time, _logger).ConfigureAwait(false);
        await _dispatcher.ResumeOnUi();
        if (read == _reads)
        {
            _free = free;
            ShowSpace();
        }
    }

    // The transfer stops at its reserve (DownloadTransfer.SpaceReserve), so a download that would reach it does not start; nor
    // does one that would take the downloads past the storage limit for downloads in Settings → Downloads.
    private void ShowSpace()
    {
        long limit = _preferences.StorageLimitGb * DownloadPreferenceLimits.BytesPerGb;
        if (limit > 0 && _needed > 0 && _taken + _needed > limit)
        {
            IsShortOfSpace = true;
            SpaceText = AppText.Get("granary.downloadOptions.overLimit", ("needed", Size(_needed)), ("left", Size(Math.Max(0, limit - _taken))),
                ("limit", Size(limit)));
            return;
        }
        if (_free is not { } free)
        {
            IsShortOfSpace = false;
            SpaceText = "";
            return;
        }
        IsShortOfSpace = _needed > 0 && free - DownloadTransfer.SpaceReserve < _needed;
        SpaceText = IsShortOfSpace
            ? AppText.Get("granary.downloadOptions.notEnoughSpace", ("needed", Size(_needed)), ("free", Size(free)))
            : AppText.Get("granary.downloadOptions.freeSpace", ("free", Size(free)));
    }

    // What a press of Download came to. Waiting: started as far as the server's limit of downloads let it, the rest waiting.
    private enum StartOutcome
    {
        Started,
        Failed,
        Limited,
        Waiting,
    }

    // A season's or a series' run stops between two episodes once the viewer stops it; a film's or an episode's one create is
    // never cut off. Each is for the profile the dialog was opened in: another one active by now (or none then) sends nothing.
    private async Task<(StartOutcome Kind, int Waiting)> StartSelectionAsync(CancellationToken stopping)
    {
        if (_openedFor is not { } scope)
        {
            return (StartOutcome.Failed, 0);
        }
        string quality = SelectedQuality?.Preset ?? DownloadQualities.Original;
        if (_season is { } season)
        {
            var result = await _downloads.StartSeasonAsync(season with { Quality = quality }, scope, stopping).ConfigureAwait(false);
            // A season cut short (another profile took over) counts none of the episodes after that: it did not all start.
            return OutcomeOf(result, season.Episodes.Count);
        }
        if (_series is { } series)
        {
            var result = await _downloads.StartSeriesAsync(series with { Quality = quality }, scope, stopping).ConfigureAwait(false);
            return OutcomeOf(result, series.EpisodeCount);
        }
        var request = _title! with { MediaFileId = SelectedVersion?.FileId ?? _title.MediaFileId, Quality = quality };
        return (await _downloads.StartAsync(request, scope, CancellationToken.None).ConfigureAwait(false) switch
        {
            DownloadStartResult.Started or DownloadStartResult.AlreadyDownloading => StartOutcome.Started,
            DownloadStartResult.Limited => StartOutcome.Limited,
            _ => StartOutcome.Failed,
        }, 0);
    }

    // A season or a series the server's limit stopped waits for the rest (the run kept them), unless another episode failed.
    private static (StartOutcome Kind, int Waiting) OutcomeOf(SeasonStartResult result, int episodes) =>
        result.ReachedLimit
            ? (result.Waiting == 0 ? StartOutcome.Limited : result.Failed == 0 ? StartOutcome.Waiting : StartOutcome.Failed, result.Waiting)
            : (result.Failed == 0 && result.Started + result.AlreadyThere == episodes ? StartOutcome.Started : StartOutcome.Failed, 0);

    // The web's "Download: {title}".
    private static string TitleWithName(string title) => AppText.Get("web.downloads.versionPicker.titleWithName", ("title", title));

    // A film by its own title; an episode as "Example Series · S1 · E5", since its own title alone does not say which one it is.
    // The codes keep Latin digits in every language.
    private static string TitleOf(DownloadRequest request)
    {
        if (request.SeriesId is null || string.IsNullOrWhiteSpace(request.SeriesTitle))
        {
            return request.Title;
        }
        var parts = new List<string> { request.SeriesTitle.Trim() };
        if (request.SeasonNumber is { } season)
        {
            parts.Add("S" + season.ToString(CultureInfo.InvariantCulture));
        }
        if (request.EpisodeNumber is { } episode)
        {
            parts.Add("E" + episode.ToString(CultureInfo.InvariantCulture));
        }
        return string.Join(" · ", parts);
    }

    private static string Size(long bytes) => bytes > 0 ? PrePlayText.FileSize(bytes) : AppText.Get("web.common.format.size.bytes", ("size", 0));
}
