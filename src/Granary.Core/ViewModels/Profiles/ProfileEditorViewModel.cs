using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Profiles;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Profiles;

/// <summary>A library a profile can be limited to, and whether it is.</summary>
public sealed partial class ProfileLibraryChoice(Library library) : ObservableObject
{
    public Library Library { get; } = library;

    public string Id => Library.Id;

    public string Name => Library.Name;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>Where an editor's writes go: the server, the profile they go out as, and the household as last read.</summary>
/// <param name="Manager">The household's primary profile, acting or lent (<see cref="ManagingProfile"/>).</param>
public sealed record ProfileEditorTarget(Uri Server, ManagingProfile Manager, ProfileCollection Household);

/// <summary>What a save did: the profile as the server has it now, and what the person may need to hear.</summary>
/// <param name="NewPin">The PIN just set, for the acting profile's new token; empty when it did not change.</param>
/// <param name="Notice">Why the picture did not change though the profile did; empty when it did.</param>
public sealed record ProfileEditorSaved(Profile Profile, bool Created, string NewPin, bool PinCleared, string Notice)
{
    // The PIN never shows in a log or a message.
    public override string ToString() => $"ProfileEditorSaved {{ Created = {Created}, NewPin = {(NewPin.Length > 0 ? "<redacted>" : "none")} }}";
}

/// <summary>
/// The profile editor, as the web's <c>ProfileEditorDialog</c> with <c>lib/profile-management.ts</c>: a new profile or one
/// to change, its name, picture (a preset or, where the server keeps uploads, an image of its own), PIN, the Kids preset and
/// the limits the server reports it takes. A name another profile has and a full household are said before anything is sent,
/// as Android's editor checks them; the writes go out as <see cref="ProfileEditorTarget.Manager"/> only.
/// </summary>
public sealed partial class ProfileEditorViewModel : ObservableObject
{
    /// <summary>The longest name the server takes.</summary>
    public const int NameMaxLength = 64;

    /// <summary>A PIN's digits.</summary>
    public const int PinLength = 4;

    private readonly ProfilesApi _profiles;
    private readonly ProfileEditorTarget _target;
    private readonly ILogger _logger;

    // Set by hand, so the Kids preset leaves it alone (applyKidsPreset).
    private bool _contentRatingTouched;
    private bool _libraryAccessTouched;
    private bool _requestsTouched;
    private bool _applyingKids;

    // The playback ceiling as stored, sent back as it is while the choice stays the one it opened on: a value this client
    // reads as no limit (a newer one) is not cleared by an unrelated edit.
    private readonly string? _storedQuality;
    private bool _qualityChanged;

    public ProfileEditorViewModel(ProfilesApi profiles, ProfileEditorTarget target, Profile? profile, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(target);
        _profiles = profiles;
        _target = target;
        _logger = logger;
        Profile = profile;
        IsPrimary = profile?.IsPrimary ?? target.Household.Items.Count == 0;
        // A ceiling the list lacks (set by a newer client) stays offered as it is stored, so an unrelated edit keeps it.
        var ratings = ProfileAccess.ContentRatings();
        ContentRatings = profile?.MaxContentRating is { Length: > 0 } stored && ratings.All(c => c.Value != stored)
            ? [.. ratings, new ProfileChoice<string>(stored, stored)]
            : ratings;
        _storedQuality = profile?.MaxPlaybackQuality;
        AdvisoryAges = ProfileAccess.AdvisoryAges();
        PlaybackCeilings = ProfileAccess.PlaybackCeilings();
        Styles = ProfileAvatarPresets.Styles();

        _applyingKids = true;
        Name = profile?.Name ?? "";
        IsChild = profile?.IsChild ?? false;
        SelectedContentRating = ContentRatings.FirstOrDefault(c => c.Value == (profile?.MaxContentRating ?? "")) ?? ContentRatings[0];
        SelectedAdvisoryAge = AdvisoryAges.FirstOrDefault(c => c.Value == profile?.MaxAdvisoryAge) ?? AdvisoryAges[0];
        RequireAdvisoryAge = profile?.RequireAdvisoryAge ?? false;
        SelectedPlaybackCeiling = PlaybackCeilings.First(c => c.Value == ProfileAccess.CeilingOf(profile?.MaxPlaybackQuality));
        // The primary profile sees every library the account can; a limit an older server stored on it goes with the next save.
        LibraryRestrictionsEnabled = !IsPrimary && (profile?.LibraryRestrictionsEnabled ?? false);
        RequestsAllowed = profile?.MayRequest ?? true;
        _qualityChanged = false;
        _applyingKids = false;

        InitializeAvatar();
    }

    /// <summary>The profile being changed; null for a new one.</summary>
    public Profile? Profile { get; }

    public bool IsNew => Profile is null;

    /// <summary>"New profile" or "Edit profile".</summary>
    public string Title => AppText.Get(IsNew ? "web.profiles.editor.titleNew" : "web.profiles.editor.titleEdit");

    /// <summary>The household's primary profile, or the first one, which becomes it: it takes no library limit.</summary>
    public bool IsPrimary { get; }

    public bool CanLimitLibraries => !IsPrimary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewName), nameof(PreviewInitial))]
    public partial string Name { get; set; }

    partial void OnNameChanged(string value) => NameError = "";

    [ObservableProperty]
    public partial string NameError { get; private set; } = "";

    /// <summary>The PIN being typed: up to four digits, anything else dropped as it is typed.</summary>
    [ObservableProperty]
    public partial string Pin { get; set; } = "";

    partial void OnPinChanged(string value)
    {
        string digits = new([.. value.Where(char.IsAsciiDigit).Take(PinLength)]);
        if (digits != value)
        {
            Pin = digits;
            return;
        }
        PinError = "";
    }

    [ObservableProperty]
    public partial string PinError { get; private set; } = "";

    /// <summary>Remove PIN was pressed: the PIN goes with the save.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinLabel), nameof(PinPlaceholder), nameof(PinToggleLabel), nameof(CanTypePin))]
    public partial bool ClearPin { get; private set; }

    /// <summary>Remove PIN / Keep existing PIN shows: the profile has a PIN.</summary>
    public bool CanClearPin => Profile?.HasPin == true;

    // Not while a save runs: the PIN said back is the one sent.
    public bool CanTypePin => !ClearPin && !IsSaving;

    public string PinLabel => AppText.Get(ClearPin ? "web.profiles.editor.pinRemoved"
        : Profile?.HasPin == true ? "web.profiles.editor.newPin" : "web.profiles.editor.pinOptional");

    public string PinPlaceholder => AppText.Get(ClearPin ? "web.profiles.editor.pinRemovedOnSave" : "web.profiles.editor.pinPlaceholder");

    public string PinToggleLabel => AppText.Get(ClearPin ? "web.profiles.editor.keepPin" : "web.profiles.editor.removePin");

    /// <summary>Remove PIN, or Keep existing PIN again.</summary>
    public void TogglePinRemoval()
    {
        if (!CanClearPin)
        {
            return;
        }
        ClearPin = !ClearPin;
        Pin = "";
        PinError = "";
    }

    public IReadOnlyList<ProfileChoice<string>> ContentRatings { get; }

    public IReadOnlyList<ProfileChoice<int?>> AdvisoryAges { get; }

    public IReadOnlyList<ProfileChoice<PlaybackCeiling>> PlaybackCeilings { get; }

    [ObservableProperty]
    public partial bool IsChild { get; set; }

    partial void OnIsChildChanged(bool value)
    {
        if (_applyingKids)
        {
            return;
        }
        _applyingKids = true;
        try
        {
            if (value)
            {
                // applyKidsPreset: a safer start, leaving what was set by hand.
                if (!_contentRatingTouched && SelectedContentRating.Value.Length == 0)
                {
                    SelectedContentRating = ContentRatings.First(c => c.Value == "PG");
                }
                if (!_libraryAccessTouched && !IsPrimary && !LibraryRestrictionsEnabled)
                {
                    LibraryRestrictionsEnabled = true;
                    SetAllLibraries(false);
                }
                if (!_requestsTouched)
                {
                    RequestsAllowed = false;
                }
            }
            else
            {
                // clearKidsPreset: every limit goes.
                _contentRatingTouched = _libraryAccessTouched = _requestsTouched = false;
                SelectedContentRating = ContentRatings[0];
                SelectedAdvisoryAge = AdvisoryAges[0];
                RequireAdvisoryAge = false;
                SelectedPlaybackCeiling = PlaybackCeilings[0];
                LibraryRestrictionsEnabled = false;
                SetAllLibraries(false);
                RequestsAllowed = true;
                LibrariesError = "";
            }
        }
        finally
        {
            _applyingKids = false;
        }
    }

    [ObservableProperty]
    public partial ProfileChoice<string> SelectedContentRating { get; set; }

    partial void OnSelectedContentRatingChanged(ProfileChoice<string> value) => _contentRatingTouched |= !_applyingKids;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRequireAdvisoryAge), nameof(AdvisoryAgeHelp))]
    public partial ProfileChoice<int?> SelectedAdvisoryAge { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AdvisoryAgeHelp))]
    public partial bool RequireAdvisoryAge { get; set; }

    [ObservableProperty]
    public partial ProfileChoice<PlaybackCeiling> SelectedPlaybackCeiling { get; set; }

    partial void OnSelectedPlaybackCeilingChanged(ProfileChoice<PlaybackCeiling> value) => _qualityChanged = true;

    // What the ceiling is sent as: the stored value while it was not changed here, else the choice's.
    private string? PlaybackQualityToSend() => !_qualityChanged && !string.IsNullOrEmpty(_storedQuality)
        ? _storedQuality
        : ProfileAccess.WireValue(SelectedPlaybackCeiling.Value);

    /// <summary>The advisory-age limit shows: the server reports it takes one.</summary>
    public bool ShowsAdvisoryAge => _target.Household.MaxAdvisoryAgeSupported;

    /// <summary>Hide titles without an advisory age shows: the server takes it, and a limit is set.</summary>
    public bool ShowsRequireAdvisoryAge => ShowsAdvisoryAge && _target.Household.RequireAdvisoryAgeSupported && SelectedAdvisoryAge.Value is not null;

    public string AdvisoryAgeHelp => AppText.Get(ShowsRequireAdvisoryAge && RequireAdvisoryAge
        ? "web.profiles.editor.advisoryAgeHelp" : "web.profiles.editor.advisoryAgeHelpFallback");

    /// <summary>Allow requests shows: the server reports it takes it.</summary>
    public bool ShowsRequests => _target.Household.RequestsAllowedSupported;

    [ObservableProperty]
    public partial bool RequestsAllowed { get; set; }

    partial void OnRequestsAllowedChanged(bool value) => _requestsTouched |= !_applyingKids;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLibraryChoices))]
    public partial bool LibraryRestrictionsEnabled { get; set; }

    partial void OnLibraryRestrictionsEnabledChanged(bool value)
    {
        _libraryAccessTouched |= !_applyingKids;
        LibrariesError = "";
    }

    public bool ShowsLibraryChoices => CanLimitLibraries && LibraryRestrictionsEnabled;

    /// <summary>The libraries the manager can see, hidden ones included, in the server's order.</summary>
    public ObservableCollection<ProfileLibraryChoice> Libraries { get; } = [];

    [ObservableProperty]
    public partial string LibrariesError { get; private set; } = "";

    /// <summary>A save runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveLabel), nameof(IsIdle), nameof(CanTypePin))]
    public partial bool IsSaving { get; private set; }

    public bool IsIdle => !IsSaving;

    public string SaveLabel => AppText.Get(IsSaving ? "web.profiles.editor.saving" : "web.profiles.editor.save");

    /// <summary>Why the save did not go through, as the web's toast or a refusal says it.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    /// <summary>Reads the libraries a profile can be limited to, as the manager; none when that fails.</summary>
    public async Task LoadLibrariesAsync(CancellationToken cancellationToken = default)
    {
        if (IsPrimary)
        {
            return;
        }
        var allowed = ProfileAccess.LibraryIds(Profile?.AllowedLibraryIds).ToHashSet(StringComparer.Ordinal);
        HasLibrariesLoadError = false;
        LibrariesError = "";
        try
        {
            var libraries = await _profiles.ListLibrariesAsync(_target.Server, _target.Manager, cancellationToken);
            Libraries.Clear();
            foreach (var library in libraries.Items.OrderBy(l => l.SortOrder))
            {
                bool chosen = Profile?.LibraryRestrictionsEnabled == true && allowed.Contains(library.Id);
                Libraries.Add(new ProfileLibraryChoice(library) { IsChecked = chosen });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The editor closed.
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning("The profile editor could not read the libraries: {Failure}", ApiFailureText.Describe(ex));
            _librariesLoadError = LibraryErrors.Describe(ex);
            LibrariesError = _librariesLoadError;
            HasLibrariesLoadError = true;
        }
    }

    private string _librariesLoadError = "";

    /// <summary>The libraries could not be read: a limit cannot be chosen, and the editor offers Retry.</summary>
    [ObservableProperty]
    public partial bool HasLibrariesLoadError { get; private set; }

    [RelayCommand]
    private Task RetryLibrariesAsync() => LoadLibrariesAsync();

    private void SetAllLibraries(bool isChecked)
    {
        foreach (var library in Libraries)
        {
            library.IsChecked = isChecked;
        }
    }

    // The web's validate: a name, a PIN of four digits when one is typed, a library when they are limited; then Android's
    // checks of what the server would refuse with a bare 409.
    private bool Validate()
    {
        NameError = Name.Trim().Length == 0 ? AppText.Get("web.profiles.editor.nameRequired")
            : NameIsTaken() ? AppText.Get("granary.profiles.editor.nameTaken") : "";
        PinError = !ClearPin && Pin.Length > 0 && Pin.Length != PinLength ? AppText.Get("web.profiles.editor.pinInvalid") : "";
        // A list that could not be read keeps saying so, rather than asking for a library it does not show.
        LibrariesError = !ShowsLibraryChoices ? ""
            : HasLibrariesLoadError ? _librariesLoadError
            : !Libraries.Any(l => l.IsChecked) ? AppText.Get("web.profiles.editor.librariesRequired") : "";
        Status = IsNew && !_target.Household.HasRoom && _target.Household.MaxProfiles is { } limit
            ? AppText.Get("web.settings.profiles.limitReached", ("count", limit))
            : "";
        return NameError.Length == 0 && PinError.Length == 0 && LibrariesError.Length == 0 && Status.Length == 0;
    }

    // Case and spaces aside, as Android's hasProfileNamed.
    private bool NameIsTaken()
    {
        string name = Name.Trim();
        return _target.Household.Items.Any(p => p.Id != Profile?.Id && string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<string> AllowedLibraryIds() =>
        ShowsLibraryChoices ? ProfileAccess.LibraryIds(Libraries.Where(l => l.IsChecked).Select(l => l.Id)) : [];
}
