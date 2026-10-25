using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api;
using Granary.Core.Api.Subtitles;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Player;

/// <summary>
/// The player's Add Subtitles dialog, as Silo's web has it (SubtitleSearchModal, SubtitleUploadForm): a file of the
/// viewer's own, its language detected and changeable, uploaded; or the server's providers searched in a language and
/// a result stored. Either way <see cref="Added"/> names the stored subtitle and the dialog is done. The player keeps
/// one for its page, so the languages chosen stay while it plays.
/// </summary>
public sealed partial class AddSubtitlesViewModel(SubtitlesApi api, ISessionManager session, ILogger<AddSubtitlesViewModel> logger) : ObservableObject
{
    /// <summary>The web's limit for an upload.</summary>
    public const long MaxFileBytes = 5 * 1024 * 1024;

    private static readonly string[] Extensions = [".srt", ".vtt", ".ass", ".ssa", ".sub"];

    // The web's list for its language selects (player/utils/languageNames.ts), shown by name.
    private static readonly string[] Languages =
    [
        "en", "es", "fr", "de", "it", "pt", "pt-BR", "pt-PT", "nl", "pl", "ru", "zh", "ja", "ko", "ar", "tr", "sv", "da", "no", "fi", "hu",
        "cs", "ro", "he", "th", "vi", "el", "bg", "hr", "sk", "sl", "uk", "id", "ms", "hi", "ta", "te", "bn", "fa",
    ];

    // web/src/player/components/SubtitleSearchModal.tsx.
    private static readonly Dictionary<string, (string Abbreviation, string Color)> ProviderLooks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["opensubtitles"] = ("OS", "#eab308"),
        ["subdl"] = ("SDL", "#3b82f6"),
        ["subsource"] = ("SS", "#ef4444"),
        ["upload"] = ("UP", "#a855f7"),
    };

    private string _mediaFileId = "";
    private byte[]? _file;
    private bool _languageOverride;
    private bool _applying;

    // Bumped when the dialog opens or closes: an answer for an earlier opening changes nothing.
    private int _generation;
    private int _detection;

    /// <summary>A subtitle was stored for the file; the dialog has closed itself.</summary>
    public event EventHandler<StoredSubtitle>? Added;

    /// <summary>Asks the page to close the dialog (Esc, ✕, done).</summary>
    public event EventHandler? CloseRequested;

    public IReadOnlyList<SettingChoice> SearchLanguages { get; } = LanguageChoices();

    [ObservableProperty]
    public partial IReadOnlyList<SettingChoice> UploadLanguages { get; private set; } = LanguageChoices();

    [ObservableProperty]
    public partial SettingChoice? UploadLanguage { get; set; }

    [ObservableProperty]
    public partial SettingChoice? SearchLanguage { get; set; }

    [ObservableProperty]
    public partial bool HearingImpaired { get; set; }

    /// <summary>"Selected: name.srt" once a file is chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile), nameof(CanUpload))]
    public partial string FileName { get; private set; } = "";

    public bool HasFile => FileName.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpload), nameof(DetectionText))]
    public partial bool IsDetecting { get; private set; }

    /// <summary>Where the language shown came from: <c>filename</c>, <c>metadata</c>, <c>content</c> or <c>manual</c>; empty before.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetectionText))]
    public partial string DetectionSource { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpload), nameof(UploadText))]
    public partial bool IsUploading { get; private set; }

    [ObservableProperty]
    public partial string UploadError { get; private set; } = "";

    /// <summary>False only when the server says it has no providers: manual upload stays.</summary>
    [ObservableProperty]
    public partial bool IsOnlineSearchEnabled { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchText), nameof(ShowsSearchHint))]
    public partial bool IsSearching { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSearchHint))]
    public partial IReadOnlyList<SubtitleSearchRow> Results { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> Warnings { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSearchHint))]
    public partial string SearchError { get; private set; } = "";

    /// <summary>A download is on its way: the rows wait for it.</summary>
    [ObservableProperty]
    public partial bool IsDownloading { get; private set; }

    public bool CanUpload => HasFile && !IsUploading && !IsDetecting;

    public string UploadText => IsUploading ? "Uploading…" : "Upload";

    public string SearchText => IsSearching ? "Searching…" : "Search";

    /// <summary>"Select a language and press Search." before a search, and after one that found nothing.</summary>
    public bool ShowsSearchHint => Results.Count == 0 && !IsSearching && SearchError.Length == 0;

    /// <summary>"Detecting language…", then "Detected German from filename"; empty for a language the viewer chose.</summary>
    public string DetectionText => IsDetecting
        ? "Detecting language…"
        : DetectionSource.Length > 0 && DetectionSource != "manual" && UploadLanguage is { } language
            ? $"Detected {language.Label} from {SourceLabel(DetectionSource)}"
            : "";

    /// <summary>Puts the dialog up afresh for <paramref name="mediaFileId"/>; the languages stay as last chosen.</summary>
    public void Open(string mediaFileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaFileId);
        _generation++;
        _mediaFileId = mediaFileId;
        _file = null;
        FileName = "";
        IsDetecting = IsUploading = IsSearching = IsDownloading = false;
        UploadError = SearchError = "";
        Results = [];
        Warnings = [];
        _applying = true;
        try
        {
            SearchLanguage ??= SearchLanguages.First(c => c.Value == "en");
            UploadLanguage = UploadLanguages.FirstOrDefault(c => c.Value == SearchLanguage.Value) ?? UploadLanguages[0];
            DetectionSource = "";
            _languageOverride = false;
            HearingImpaired = false;
        }
        finally
        {
            _applying = false;
        }
        _ = ReadProvidersAsync(_generation);
    }

    /// <summary>The file picker or a drop did not hand over a readable file.</summary>
    public void FileCouldNotBeOpened() => UploadError = "The file could not be read.";

    [RelayCommand]
    private void Close()
    {
        _generation++;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The file the viewer browsed for or dropped: a subtitle file of up to 5 MB, read and its language detected.</summary>
    public async Task SelectFileAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string name = Path.GetFileName(path);
        if (!Extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
        {
            UploadError = "Unsupported file type. Use SRT, VTT, ASS, SSA, or SUB.";
            return;
        }
        int generation = _generation;
        byte[] content;
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                UploadError = "The file is larger than 5 MB.";
                return;
            }
            content = await File.ReadAllBytesAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException)
        {
            logger.LogInformation("A subtitle file could not be read: {Failure}", ex.GetType().Name);
            FileCouldNotBeOpened();
            return;
        }
        if (generation != _generation)
        {
            return;
        }
        _file = content;
        FileName = name;
        UploadError = "";
        await DetectLanguageAsync(name, content);
    }

    // Not held by a request of an earlier opening, which no longer counts.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task UploadAsync()
    {
        if (_file is not { } content || !CanUpload || UploadLanguage is not { } language || Server is not { } server)
        {
            return;
        }
        int generation = _generation;
        IsUploading = true;
        UploadError = "";
        try
        {
            var stored = await api.UploadAsync(server, new SubtitleUpload(_mediaFileId, FileName, content, language.Value, _languageOverride, HearingImpaired));
            if (generation == _generation)
            {
                Done(stored);
            }
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            logger.LogInformation("The subtitle upload failed: {Failure}", ApiFailureText.Describe(ex));
            if (generation == _generation)
            {
                UploadError = Failure(ex, "Upload failed");
            }
        }
        finally
        {
            if (generation == _generation)
            {
                IsUploading = false;
            }
        }
    }

    // Not held by a request of an earlier opening, which no longer counts.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SearchAsync()
    {
        if (SearchLanguage is not { } language || Server is not { } server || IsSearching)
        {
            return;
        }
        int generation = _generation;
        IsSearching = true;
        SearchError = "";
        Results = [];
        Warnings = [];
        try
        {
            var found = await api.SearchAsync(server, _mediaFileId, language.Value);
            if (generation == _generation)
            {
                Results = [.. found.Results.Select(Row)];
                Warnings = found.Warnings;
            }
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            logger.LogInformation("The subtitle search failed: {Failure}", ApiFailureText.Describe(ex));
            if (generation == _generation)
            {
                SearchError = Failure(ex, "Search failed");
            }
        }
        finally
        {
            if (generation == _generation)
            {
                IsSearching = false;
            }
        }
    }

    /// <summary>Stores <paramref name="row"/>'s subtitle for the file; one at a time.</summary>
    public async Task DownloadAsync(SubtitleSearchRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (IsDownloading || Server is not { } server)
        {
            return;
        }
        int generation = _generation;
        IsDownloading = true;
        row.IsDownloading = true;
        foreach (var each in Results)
        {
            each.CanDownload = false;
        }
        SearchError = "";
        try
        {
            var stored = await api.DownloadAsync(server, _mediaFileId, row.Result);
            if (generation == _generation)
            {
                Done(stored);
            }
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            logger.LogInformation("The subtitle download failed: {Failure}", ApiFailureText.Describe(ex));
            if (generation == _generation)
            {
                SearchError = Failure(ex, "Download failed");
            }
        }
        finally
        {
            row.IsDownloading = false;
            if (generation == _generation)
            {
                IsDownloading = false;
                foreach (var each in Results)
                {
                    each.CanDownload = true;
                }
            }
        }
    }

    private Uri? Server => session.ActiveServer?.BaseUri;

    private void Done(StoredSubtitle stored)
    {
        _generation++;
        CloseRequested?.Invoke(this, EventArgs.Empty);
        // Stored either way; one without an id cannot be named to the player.
        if (stored.Id.Length > 0)
        {
            Added?.Invoke(this, stored);
        }
    }

    // Fails open, as the web does: only an explicit "not enabled" hides the search; an older server or a failed read keeps it.
    private async Task ReadProvidersAsync(int generation)
    {
        if (Server is not { } server)
        {
            return;
        }
        IsOnlineSearchEnabled = true;
        try
        {
            var status = await api.GetProviderStatusAsync(server);
            if (generation == _generation)
            {
                IsOnlineSearchEnabled = status.Enabled;
            }
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            logger.LogInformation("The subtitle providers could not be read: {Failure}", ApiFailureText.Describe(ex));
        }
    }

    private async Task DetectLanguageAsync(string name, byte[] content)
    {
        if (Server is not { } server)
        {
            return;
        }
        int generation = _generation;
        int detection = ++_detection;
        IsDetecting = true;
        try
        {
            var detected = await api.DetectLanguageAsync(server, name, content, UploadLanguage?.Value);
            if (generation != _generation || detection != _detection || detected.Language.Length == 0)
            {
                return;
            }
            ShowLanguage(detected.Language);
            DetectionSource = detected.Source;
            _languageOverride = false;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            logger.LogInformation("The subtitle language could not be detected: {Failure}", ApiFailureText.Describe(ex));
            if (generation == _generation && detection == _detection)
            {
                DetectionSource = "";
                UploadError = Failure(ex, "Failed to detect subtitle language");
            }
        }
        finally
        {
            if (generation == _generation && detection == _detection)
            {
                IsDetecting = false;
            }
        }
    }

    // A language the list does not have (the server detected another one) joins it, so the select shows it.
    private void ShowLanguage(string tag)
    {
        _applying = true;
        try
        {
            if (UploadLanguages.FirstOrDefault(c => string.Equals(c.Value, tag, StringComparison.OrdinalIgnoreCase)) is { } known)
            {
                UploadLanguage = known;
                return;
            }
            var added = new SettingChoice(tag, TrackText.LanguageName(tag));
            UploadLanguages = [.. UploadLanguages.Append(added).OrderBy(c => c.Label, StringComparer.CurrentCulture)];
            UploadLanguage = added;
        }
        finally
        {
            _applying = false;
        }
    }

    // A language the viewer chose is kept by the server as it is.
    partial void OnUploadLanguageChanged(SettingChoice? value)
    {
        if (!_applying && value is not null)
        {
            DetectionSource = "manual";
            _languageOverride = true;
        }
        OnPropertyChanged(nameof(DetectionText));
    }

    private static IReadOnlyList<SettingChoice> LanguageChoices() =>
        [.. Languages.Select(code => new SettingChoice(code, TrackText.LanguageName(code))).OrderBy(c => c.Label, StringComparer.CurrentCulture)];

    private static string SourceLabel(string source) => source switch
    {
        "filename" => "filename",
        "metadata" => "file metadata",
        "content" => "subtitle text",
        "manual" => "manual selection",
        _ => "detection",
    };

    // The server's own words when it gave some, as the web shows them.
    private static string Failure(Exception ex, string fallback) => ex switch
    {
        SiloApiException { Problem.Detail: { Length: > 0 } detail } => detail,
        SiloApiException => fallback,
        _ => ApiFailureText.ForTransport(ex),
    };

    private static SubtitleSearchRow Row(SubtitleSearchResult result)
    {
        var (abbreviation, color) = ProviderLooks.TryGetValue(result.Provider, out var look) ? look : (result.Provider, "#9ca3af");
        return new SubtitleSearchRow(result, abbreviation, color);
    }
}

/// <summary>
/// A result of the online search as its row shows it: the score badge (green from 70, yellow from 40, else red), the
/// release, HI, the downloads and the provider's badge.
/// </summary>
public sealed partial class SubtitleSearchRow(SubtitleSearchResult result, string providerText, string providerColor) : ObservableObject
{
    public SubtitleSearchResult Result { get; } = result;

    public string ScoreText { get; } = result.Score.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The score's colour, <c>#rrggbb</c>.</summary>
    public string ScoreColor { get; } = result.Score >= 70 ? "#22c55e" : result.Score >= 40 ? "#eab308" : "#ef4444";

    public string ReleaseName { get; } = result.ReleaseName;

    public bool IsHearingImpaired { get; } = result.HearingImpaired;

    /// <summary>"↓12,345".</summary>
    public string DownloadsText { get; } = "↓" + result.Downloads.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"OS", "SDL", "SS", "UP", or the provider's own name.</summary>
    public string ProviderText { get; } = providerText;

    public string ProviderColor { get; } = providerColor;

    /// <summary>"Score 87, Release.Name, hearing impaired, 12,345 downloads, OpenSubtitles".</summary>
    public string AccessibleName =>
        $"Score {ScoreText}, {ReleaseName}{(IsHearingImpaired ? ", hearing impaired" : "")}, {DownloadsText[1..]} downloads, {Result.Provider}";

    [ObservableProperty]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    public partial bool CanDownload { get; set; } = true;
}
