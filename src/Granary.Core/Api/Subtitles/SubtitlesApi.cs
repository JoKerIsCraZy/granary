using System.Globalization;
using System.Net.Http.Headers;

namespace Granary.Core.Api.Subtitles;

/// <summary>
/// Adding subtitles to a file, as Silo's web player does in its Add Subtitles dialog: searching the server's subtitle
/// providers and storing a result, uploading a file of the viewer's own, and guessing the language of that file first.
/// Every subtitle stored this way joins the file's inventory, which the next plan lists.
/// </summary>
public sealed class SubtitlesApi(SiloHttp http)
{
    /// <summary>Whether the server searches providers at all; manual upload never depends on it.</summary>
    public Task<SubtitleProviderStatus> GetProviderStatusAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/subtitles/providers/status", SiloAuth.Profile, SubtitlesJsonContext.Default.SubtitleProviderStatus, cancellationToken);

    /// <summary>The providers' subtitles for <paramref name="mediaFileId"/> in <paramref name="language"/>, best match first.</summary>
    public Task<SubtitleSearchResults> SearchAsync(Uri server, string mediaFileId, string language, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaFileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        var body = new SubtitleSearchBody { MediaFileId = mediaFileId, Languages = [language] };
        return http.PostAsync(server, "api/v2/subtitles/search", SiloAuth.Profile, body,
            SubtitlesJsonContext.Default.SubtitleSearchBody, SubtitlesJsonContext.Default.SubtitleSearchResults, cancellationToken);
    }

    /// <summary>Stores <paramref name="result"/> for <paramref name="mediaFileId"/>, echoing the search's own fields.</summary>
    public async Task<StoredSubtitle> DownloadAsync(Uri server, string mediaFileId, SubtitleSearchResult result, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaFileId);
        ArgumentNullException.ThrowIfNull(result);
        var body = new SubtitleDownloadBody
        {
            MediaFileId = mediaFileId,
            Provider = result.Provider,
            SubtitleId = result.Id,
            Language = result.Language,
            ReleaseName = result.ReleaseName,
            Score = result.Score,
            HearingImpaired = result.HearingImpaired,
        };
        var stored = await http.PostAsync(server, "api/v2/subtitles/download", SiloAuth.Profile, body,
            SubtitlesJsonContext.Default.SubtitleDownloadBody, SubtitlesJsonContext.Default.SubtitleDownloadResult, cancellationToken).ConfigureAwait(false);
        return stored.Subtitle;
    }

    /// <summary>
    /// Stores the viewer's own file. <see cref="SubtitleUpload.LanguageOverride"/> says the language was chosen, not
    /// detected, so the server keeps it.
    /// </summary>
    public async Task<StoredSubtitle> UploadAsync(Uri server, SubtitleUpload upload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentException.ThrowIfNullOrWhiteSpace(upload.MediaFileId);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(upload.MediaFileId), "media_file_id" },
            { File(upload.Content), "file", upload.FileName },
            { new StringContent(upload.Language), "language" },
            { new StringContent(Flag(upload.LanguageOverride)), "language_override" },
            { new StringContent(Flag(upload.HearingImpaired)), "hearing_impaired" },
        };
        var stored = await http.PostFormAsync(server, "api/v2/subtitles/upload", SiloAuth.Profile, form,
            SubtitlesJsonContext.Default.SubtitleDownloadResult, cancellationToken).ConfigureAwait(false);
        return stored.Subtitle;
    }

    /// <summary>
    /// The language of a subtitle file, from its name, its metadata or its text; <paramref name="hint"/> is the one
    /// shown so far, which the server falls back to.
    /// </summary>
    public async Task<SubtitleDetection> DetectLanguageAsync(Uri server, string fileName, ReadOnlyMemory<byte> content, string? hint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        using var form = new MultipartFormDataContent { { File(content), "file", fileName } };
        if (!string.IsNullOrWhiteSpace(hint))
        {
            form.Add(new StringContent(hint), "language");
        }
        return await http.PostFormAsync(server, "api/v2/subtitles/detect-language", SiloAuth.Profile, form,
            SubtitlesJsonContext.Default.SubtitleDetection, cancellationToken).ConfigureAwait(false);
    }

    private static ReadOnlyMemoryContent File(ReadOnlyMemory<byte> content)
    {
        var part = new ReadOnlyMemoryContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return part;
    }

    private static string Flag(bool value) => value.ToString(CultureInfo.InvariantCulture).ToLowerInvariant();
}

/// <summary>A file the viewer chose to upload for <see cref="MediaFileId"/>, read into memory (at most 5 MB).</summary>
public sealed record SubtitleUpload(string MediaFileId, string FileName, ReadOnlyMemory<byte> Content, string Language, bool LanguageOverride,
    bool HearingImpaired);

/// <summary>What the server can search: <see cref="Enabled"/> false hides online search.</summary>
public sealed record SubtitleProviderStatus
{
    public bool Allowed { get; init; }

    public bool Enabled { get; init; }

    /// <summary><c>available</c>, <c>disabled</c>, <c>not_configured</c> or <c>unsupported</c>.</summary>
    public string State { get; init => field = value ?? ""; } = "";

    public IReadOnlyList<string> Providers { get; init => field = value ?? []; } = [];
}

/// <summary>One provider's subtitle; <see cref="Id"/> is that provider's own, echoed back on download.</summary>
public sealed record SubtitleSearchResult
{
    public string Id { get; init => field = value ?? ""; } = "";

    /// <summary><c>opensubtitles</c>, <c>subdl</c>, <c>subsource</c>, …</summary>
    public string Provider { get; init => field = value ?? ""; } = "";

    public string Language { get; init => field = value ?? ""; } = "";

    public string ReleaseName { get; init => field = value ?? ""; } = "";

    public string Format { get; init => field = value ?? ""; } = "";

    /// <summary>How well it matches the file, 0 to 100.</summary>
    public double Score { get; init; }

    public long Downloads { get; init; }

    public bool HearingImpaired { get; init; }
}

public sealed record SubtitleSearchResults
{
    public IReadOnlyList<SubtitleSearchResult> Results { get; init => field = value ?? []; } = [];

    /// <summary>A provider that failed or was skipped, in the server's words.</summary>
    public IReadOnlyList<string> Warnings { get; init => field = value ?? []; } = [];
}

/// <summary>A subtitle the server keeps for a file, downloaded or uploaded.</summary>
public sealed record StoredSubtitle
{
    public string Id { get; init => field = value ?? ""; } = "";

    public string MediaFileId { get; init => field = value ?? ""; } = "";

    public string Provider { get; init => field = value ?? ""; } = "";

    public string Language { get; init => field = value ?? ""; } = "";

    public string Format { get; init => field = value ?? ""; } = "";

    public string ReleaseName { get; init => field = value ?? ""; } = "";

    public bool HearingImpaired { get; init; }
}

/// <summary>A file's language, and where it was read: <c>filename</c>, <c>metadata</c>, <c>content</c> or <c>manual</c>.</summary>
public sealed record SubtitleDetection
{
    public string Language { get; init => field = value ?? ""; } = "";

    public string Source { get; init => field = value ?? ""; } = "";
}

internal sealed record SubtitleSearchBody
{
    public required string MediaFileId { get; init; }

    public required IReadOnlyList<string> Languages { get; init; }
}

internal sealed record SubtitleDownloadBody
{
    public required string MediaFileId { get; init; }

    public required string Provider { get; init; }

    public required string SubtitleId { get; init; }

    public required string Language { get; init; }

    public required string ReleaseName { get; init; }

    public double Score { get; init; }

    public bool HearingImpaired { get; init; }
}

internal sealed record SubtitleDownloadResult
{
    public StoredSubtitle Subtitle { get; init => field = value ?? new StoredSubtitle(); } = new();
}
