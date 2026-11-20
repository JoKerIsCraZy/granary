using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api.Admin;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// An account's own policy as the web's <c>PolicyAccessFields</c> and <c>PolicyLimitFields</c> edit it
/// (<c>components/UserPolicyFields.tsx</c>): the libraries, downloads and requests on the Access tab, the stream limits,
/// transcoding, quality and profile limit on the Limits tab. A field left on its default sends nothing, and each default
/// names where it comes from and what it is now ("Inherited: Unlimited"), following the role and group picked.
/// </summary>
public sealed partial class AdminPolicyForm : ObservableObject
{
    private AdminPolicySource _source;
    private AdminPolicyValues? _effective;

    public AdminPolicyForm()
    {
        QualityOptions = [new(""), new(AppText.Get("web.profiles.helpers.quality.any")), new(AppText.Get("web.profiles.helpers.quality.standard")),
            new(QualityFourK)];
        Hint(AdminPolicySource.Group, null);
    }

    /// <summary>A resolution name, the same in every language, as the web's <c>QUALITY_4K</c>.</summary>
    private const string QualityFourK = "4K";

    public AdminPolicySwitchField Downloads { get; } = new("web.profiles.userPolicy.downloads");

    public AdminPolicySwitchField DownloadTranscodes { get; } = new("web.profiles.userPolicy.downloadTranscodes");

    public AdminPolicySwitchField Requests { get; } = new("web.profiles.userPolicy.mediaRequests", "web.profiles.userPolicy.mediaRequestsHint");

    public AdminPolicyLimitField MaxStreams { get; } = new("web.profiles.userPolicy.maxStreams");

    public AdminPolicyLimitField MaxTranscodes { get; } = new("web.profiles.userPolicy.maxTranscodes");

    public AdminPolicyBitrateField MaxRemoteBitrate { get; } = new("web.profiles.userPolicy.maxRemoteBitrate");

    public AdminPolicyBitrateField MaxLocalBitrate { get; } = new("web.profiles.userPolicy.maxLocalBitrate");

    public AdminPolicySwitchField VideoTranscoding { get; } = new("web.profiles.userPolicy.videoTranscoding");

    public AdminPolicySwitchField AudioTranscoding { get; } = new("web.profiles.userPolicy.audioTranscoding", "web.profiles.userPolicy.audioTranscodingHint");

    public AdminPolicyLimitField MaxProfiles { get; } = new("web.profiles.userPolicy.maxProfiles", min: 1);

    /// <summary>The default ("Inherited: Any"), Any, Standard and 4K.</summary>
    public IReadOnlyList<AdminPolicyOption> QualityOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityHint))]
    public partial int QualityIndex { get; set; }

    /// <summary>Under the quality menu: where the default comes from, or what the choice allows.</summary>
    public string QualityHint => QualityIndex switch
    {
        1 => AppText.Get("web.profiles.helpers.quality.anyDescription"),
        2 => AppText.Get("web.profiles.helpers.quality.standardDescription"),
        3 => AppText.Get("web.profiles.helpers.quality.fourKDescription"),
        _ => AdminPolicyText.Quality(_source),
    };

    /// <summary>The account takes its default libraries; off, the ones switched on below.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChoosesLibraries), nameof(HasNoLibraries))]
    public partial bool InheritLibraries { get; set; } = true;

    public bool ChoosesLibraries => !InheritLibraries;

    /// <summary>The server's libraries, in its order.</summary>
    public ObservableCollection<AdminLibraryChoice> Libraries { get; } = [];

    /// <summary>"No libraries available.", while choosing with none read.</summary>
    public bool HasNoLibraries => !InheritLibraries && Libraries.Count == 0;

    /// <summary>Beside the libraries' switch: "Inherit from group", "Admin default" or "Server default".</summary>
    public string InheritLibrariesText => AdminPolicyText.AllLibraries(_source);

    /// <summary>Under it while on: "Inherited: 2 libraries".</summary>
    public string LibraryHint => AdminPolicyText.Hint(_source, _effective is null ? null
        : _effective.LibraryIds is not { } ids ? AppText.Get("web.profiles.libraryAccess.allLibraries")
        : AppText.Get("web.profiles.libraries.count", ("count", ids.Count), ("count_text", ids.Count)));

    /// <summary>The web's <c>DownloadsServerNotice</c>: the key of what a server switch overrides, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDownloadsNotice))]
    public partial string? DownloadsNoticeKey { get; private set; }

    public bool HasDownloadsNotice => DownloadsNoticeKey is not null;

    /// <summary>A limit is overridden without a valid value: the dialog shows its Limits tab instead of saving.</summary>
    public bool HasInvalidLimit => MaxStreams.IsInvalid || MaxTranscodes.IsInvalid || MaxRemoteBitrate.IsInvalid || MaxLocalBitrate.IsInvalid
        || MaxProfiles.IsInvalid;

    /// <summary>Every field back on its default, with the server's libraries to choose from.</summary>
    public void Reset(IEnumerable<AdminLibraryChoice> libraries)
    {
        foreach (var field in new[] { Downloads, DownloadTranscodes, Requests, VideoTranscoding, AudioTranscoding })
        {
            field.Reset();
        }
        foreach (var field in new[] { MaxStreams, MaxTranscodes, MaxProfiles })
        {
            field.Reset();
        }
        MaxRemoteBitrate.Reset();
        MaxLocalBitrate.Reset();
        QualityIndex = 0;
        InheritLibraries = true;
        ShowLibraries(libraries);
    }

    /// <summary>The server's libraries, read after the dialog opened.</summary>
    public void ShowLibraries(IEnumerable<AdminLibraryChoice> libraries)
    {
        Libraries.Clear();
        foreach (var library in libraries)
        {
            Libraries.Add(library);
        }
        OnPropertyChanged(nameof(HasNoLibraries));
    }

    /// <summary>Which of the server's download switches is off: downloads first, as the web's notice names only that one then.</summary>
    public void ShowServerSwitches(AdminServerSwitches? switches) => DownloadsNoticeKey = switches switch
    {
        null => null,
        { DownloadsEnabled: false } => "web.admin.common.downloadAccess.serverOff",
        { DownloadConversionEnabled: false } => "web.admin.common.downloadAccess.conversionOff",
        _ => null,
    };

    /// <summary>
    /// Where the defaults come from and what they are now, as the web's <c>policyInheritHints</c>: the admin's built-in
    /// layer, the server's for no group, the group's. <paramref name="effective"/> is null while that is unknown.
    /// </summary>
    public void Hint(AdminPolicySource source, AdminPolicyValues? effective)
    {
        (_source, _effective) = (source, effective);
        Downloads.Hint(source, effective?.DownloadAllowed);
        DownloadTranscodes.Hint(source, effective?.DownloadTranscodeAllowed);
        Requests.Hint(source, effective?.RequestsAllowed);
        VideoTranscoding.Hint(source, effective?.TranscodeAllowed);
        AudioTranscoding.Hint(source, effective?.AudioTranscodeAllowed);
        MaxStreams.Hint(source, effective?.MaxStreams);
        MaxTranscodes.Hint(source, effective?.MaxTranscodes);
        MaxRemoteBitrate.Hint(source, effective?.MaxRemoteStreamBitrateKbps);
        MaxLocalBitrate.Hint(source, effective?.MaxLocalStreamBitrateKbps);
        MaxProfiles.Hint(source, effective?.MaxProfiles);
        QualityOptions[0].Text = AdminPolicyText.Hint(source, effective is null ? null : QualityName(effective.MaxPlaybackQuality));
        OnPropertyChanged(nameof(QualityHint));
        OnPropertyChanged(nameof(InheritLibrariesText));
        OnPropertyChanged(nameof(LibraryHint));
    }

    /// <summary>A new account's draft with the overrides set here; the rest left out, to inherit.</summary>
    public AdminUserDraft ApplyTo(AdminUserDraft draft) => new()
    {
        Username = draft.Username,
        Email = draft.Email,
        Password = draft.Password,
        RequirePasswordChange = draft.RequirePasswordChange,
        Role = draft.Role,
        Permissions = draft.Permissions,
        CreateDefaultProfile = draft.CreateDefaultProfile,
        AccessGroupId = draft.AccessGroupId,
        LibraryIds = InheritLibraries ? null : [.. Libraries.Where(library => library.IsChecked).Select(library => library.Id)],
        MaxPlaybackQuality = QualityIndex switch
        {
            1 => "",
            2 => "1080p",
            3 => "2160p",
            _ => null,
        },
        MaxStreams = MaxStreams.Value,
        MaxTranscodes = MaxTranscodes.Value,
        MaxRemoteStreamBitrateKbps = MaxRemoteBitrate.Value,
        MaxLocalStreamBitrateKbps = MaxLocalBitrate.Value,
        MaxProfiles = MaxProfiles.Value,
        TranscodeAllowed = VideoTranscoding.Value,
        AudioTranscodeAllowed = AudioTranscoding.Value,
        DownloadAllowed = Downloads.Value,
        DownloadTranscodeAllowed = DownloadTranscodes.Value,
        RequestsAllowed = Requests.Value,
    };

    // A menu clears its choice when its items change; the field keeps its own.
    partial void OnQualityIndexChanged(int oldValue, int newValue)
    {
        if (newValue < 0)
        {
            QualityIndex = oldValue;
        }
    }

    // Choosing starts from every library switched on, as the web's selector does.
    partial void OnInheritLibrariesChanged(bool value)
    {
        if (!value)
        {
            foreach (var library in Libraries)
            {
                library.IsChecked = true;
            }
        }
    }

    // The web's formatPlaybackQualityPreset: 1080p and lower are Standard, 2160p and higher 4K, anything else Any.
    private static string QualityName(string value) => value.Trim().ToLowerInvariant() switch
    {
        "standard" or "480p" or "720p" or "1080p" => AppText.Get("web.profiles.helpers.quality.standard"),
        "4k" or "uhd" or "2160p" or "4320p" => QualityFourK,
        _ => AppText.Get("web.profiles.helpers.quality.any"),
    };
}
