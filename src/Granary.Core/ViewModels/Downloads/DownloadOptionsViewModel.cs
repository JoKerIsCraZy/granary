using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
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
/// The download dialog of a film, an episode or a season. Its title, texts and version rows are the web's
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
    public const string Description = "Choose a file to download. Make sure you have enough disk space.";
    public const string Footer = "Larger files require more storage space.";
    public const string StartFailed = "Download could not be started. Check access and try again.";

    /// <summary>The web's words while a card's dialog reads its item (<c>web.item.menu.loadingDetails</c>).</summary>
    public const string LoadingDetails = "Loading the latest item details…";

    /// <summary>The web's words when that read failed (<c>web.item.menu.detailsFailed</c>).</summary>
    public const string DetailsFailed = "The item details could not be loaded.";

    // The dialog's title before it knows what it offers (web: web.downloads.versionPicker.title).
    private const string PlainTitle = "Download";

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

    private DownloadRequest? _title;
    private SeasonDownloadRequest? _season;
    private long _seasonBytes;
    private int _runtimeSeconds;
    private Dictionary<string, DownloadQualityOption> _options = new(StringComparer.Ordinal);

    // The bytes the selection needs, 0 when not known; the free bytes once read, null until then or when the drive is not there.
    private long _needed;
    private long? _free;
    private int _reads;

    // A card's read of what to offer, and the one under way (Try Again starts another, the dialog's close stops it).
    private Func<CancellationToken, Task<DownloadOffer>>? _read;
    private CancellationTokenSource? _reading;
    private int _offerReads;

    public DownloadOptionsViewModel(DownloadManager downloads, DownloadsFeature feature, IDownloadPreferences preferences, IDiskSpace disk,
        IUiDispatcher dispatcher, TimeProvider time, ILogger<DownloadOptionsViewModel> logger)
    {
        _downloads = downloads;
        _feature = feature;
        _preferences = preferences;
        _disk = disk;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// "Download: Example Movie", "Download: Example Series · S1 · E5", "Download: Example Series · Season 1"; "Download" while
    /// a card's dialog does not know its title yet.
    /// </summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = PlainTitle;

    /// <summary>The title's files, the highest resolution first; none for a season.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsVersions), nameof(ShowsFooter))]
    public partial IReadOnlyList<DownloadVersionRow> Versions { get; private set; } = [];

    /// <summary>
    /// There is something to download: a title with a file, or a season's episodes. A card's dialog has it once it read its
    /// title, and only when there is.
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

    /// <summary>"8.0 GB" for the original, "About 2.1 GB" for a preset, "10 episodes · 21.4 GB" for a season.</summary>
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
    /// Download can be pressed: there is something to download, nothing is starting and the selection fits, as far as the free
    /// space is known yet.
    /// </summary>
    public bool CanStart => HasOffer && !IsStarting && !IsShortOfSpace;

    /// <summary>Why the last press of Download did not start it; empty when it did, or before.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorText { get; private set; } = "";

    public bool HasError => ErrorText.Length > 0;

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
        Open(Take(new SeasonDownloadOffer(request, seasonTitle, knownBytes)));
    }

    /// <summary>
    /// A card's film, episode or season, whose files the card does not carry: the dialog opens with "Download" and the web's
    /// loading words while <paramref name="read"/> reads what to offer, then shows it as <see cref="ForTitle"/> or
    /// <see cref="ForSeason"/> would, or why there is nothing to download, or that the read failed, with Try Again.
    /// </summary>
    public void ForReading(Func<CancellationToken, Task<DownloadOffer>> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        _read = read;
        _title = null;
        _season = null;
        _seasonBytes = 0;
        _runtimeSeconds = 0;
        Title = PlainTitle;
        Versions = [];
        HasOffer = false;
        Open(null);
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
    /// season whose episodes did not all start is false; pressing again sends only the ones still missing.
    /// </summary>
    public async Task<bool> StartAsync()
    {
        if (IsStarting || (_title is null && _season is null))
        {
            return false;
        }
        IsStarting = true;
        ErrorText = "";
        try
        {
            await SpaceRead;
            await _dispatcher.ResumeOnUi();
            if (IsShortOfSpace)
            {
                return false;
            }
            bool started;
            try
            {
                started = await StartSelectionAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Whatever went wrong, the dialog says it did not start, and Download can be pressed again.
                _logger.LogWarning("A download could not be started ({Failure})", DownloadLog.Failure(ex));
                started = false;
            }
            await _dispatcher.ResumeOnUi();
            if (!started)
            {
                ErrorText = StartFailed;
            }
            return started;
        }
        finally
        {
            IsStarting = false;
        }
    }

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

    // What the offer downloads, its title and files; answers the version to choose first, none for a season.
    private DownloadVersionRow? Take(DownloadOffer offer)
    {
        _title = (offer as TitleDownloadOffer)?.Request;
        _season = (offer as SeasonDownloadOffer)?.Request;
        _seasonBytes = offer is SeasonDownloadOffer { KnownBytes: var bytes } ? Math.Max(0, bytes) : 0;
        _runtimeSeconds = offer is TitleDownloadOffer { RuntimeSeconds: var seconds } ? Math.Max(0, seconds) : 0;
        switch (offer)
        {
            case TitleDownloadOffer title:
                Title = "Download: " + TitleOf(title.Request);
                Versions = title.Versions;
                HasOffer = true;
                return title.Versions.FirstOrDefault(v => v.FileId == title.Request.MediaFileId) ?? title.Versions.FirstOrDefault();
            case SeasonDownloadOffer season:
                string name = string.IsNullOrWhiteSpace(season.SeasonTitle)
                    ? "Season " + season.Request.SeasonNumber.ToString(CultureInfo.InvariantCulture)
                    : season.SeasonTitle.Trim();
                Title = $"Download: {season.Request.SeriesTitle} · {name}";
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
        DescriptionText = Description;
        SelectedVersion = version;
        ShowSize();
    }

    // The rows as the dialog opens: the server's qualities with the viewer's default chosen, the size, and a fresh read of the space.
    private void Open(DownloadVersionRow? version)
    {
        ErrorText = "";
        var options = OfferedQualities();
        _options = options.ToDictionary(o => o.Preset, StringComparer.Ordinal);
        Qualities = [.. options.Select(o => new DownloadQualityRow(o.Preset, DownloadText.QualityLabel(o)))];
        SelectedVersion = version;
        SelectedQuality = Qualities.FirstOrDefault(q => q.Preset == _preferences.DefaultQuality)
            ?? Qualities.FirstOrDefault(q => q.Preset == DownloadQualities.Original)
            ?? Qualities[0];
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
        (SizeText, _needed) = _season is { } season ? SeasonSize(season) : TitleSize();
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
        return ("About " + PrePlayText.FileSize(estimate), estimate);
    }

    // A season's episodes and, for the original, what their files take; a preset's files are not known before the server makes them.
    private (string Text, long Bytes) SeasonSize(SeasonDownloadRequest season)
    {
        int count = season.Episodes.Count;
        string episodes = count == 1 ? "1 episode" : count.ToString(CultureInfo.InvariantCulture) + " episodes";
        return IsOriginal && _seasonBytes > 0 ? ($"{episodes} · {PrePlayText.FileSize(_seasonBytes)}", _seasonBytes) : (episodes, 0);
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

    // The transfer stops at its reserve (DownloadTransfer.SpaceReserve), so a download that would reach it does not start.
    private void ShowSpace()
    {
        if (_free is not { } free)
        {
            IsShortOfSpace = false;
            SpaceText = "";
            return;
        }
        IsShortOfSpace = _needed > 0 && free - DownloadTransfer.SpaceReserve < _needed;
        SpaceText = IsShortOfSpace
            ? $"Not enough free space on this PC: {Size(_needed)} needed, {Size(free)} free."
            : $"{Size(free)} free on this PC.";
    }

    private async Task<bool> StartSelectionAsync()
    {
        string quality = SelectedQuality?.Preset ?? DownloadQualities.Original;
        if (_season is { } season)
        {
            var result = await _downloads.StartSeasonAsync(season with { Quality = quality }, CancellationToken.None).ConfigureAwait(false);
            // A season cut short (another profile took over) counts none of the episodes after that: it did not all start.
            return result.Failed == 0 && result.Started + result.AlreadyThere == season.Episodes.Count;
        }
        var request = _title! with { MediaFileId = SelectedVersion?.FileId ?? _title.MediaFileId, Quality = quality };
        return await _downloads.StartAsync(request, CancellationToken.None).ConfigureAwait(false)
            is DownloadStartResult.Started or DownloadStartResult.AlreadyDownloading;
    }

    // A film by its own title; an episode as "Example Series · S1 · E5", since its own title alone does not say which one it is.
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

    private static string Size(long bytes) => bytes > 0 ? PrePlayText.FileSize(bytes) : "0 B";
}
