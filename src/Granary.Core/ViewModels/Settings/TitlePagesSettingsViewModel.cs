using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Settings;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Title Pages, as Silo's web has it (<c>pages/settings/TitlePagesSettings.tsx</c>): its Title group, as the web's
/// <c>TitleArtSettingsGroup.tsx</c>, its Sound (theme music), its Spoilers, the profile's two switches
/// (<see cref="EpisodeSpoilerSettings"/>), and its Age guidance (<see cref="TitlePageSettings"/>), each saved at once. While
/// the spoiler switches cannot be edited, the page says why instead; a group the server does not keep is left out.
/// </summary>
public sealed partial class TitlePagesSettingsViewModel : ObservableObject, IDisposable
{
    public const string UnavailableTitle = "Couldn't load your spoiler settings";
    public const string UnavailableText = "Editing is off until they load, so nothing you chose earlier gets overwritten.";
    public const string UnsupportedTitle = "Your server needs an update";
    public const string UnsupportedText = "Spoiler settings can be changed once your server runs a newer version of Silo.";

    /// <summary>The web's words when a switch did not save.</summary>
    public const string SaveFailedText = "Failed to save title page setting";

    /// <summary>The web's words when the title art did not save.</summary>
    public const string TitleArtSaveFailedText = "Could not save the title art setting";

    private readonly EpisodeSpoilerSettings _settings;
    private readonly TitlePageSettings? _titlePage;

    public TitlePagesSettingsViewModel(EpisodeSpoilerSettings settings, TitlePageSettings? titlePage = null)
    {
        _settings = settings;
        _titlePage = titlePage;
        _settings.PropertyChanged += OnSettingsChanged;
        _titlePage?.PropertyChanged += OnTitlePageChanged;
    }

    /// <summary>"Blur unwatched episode images"; the switch saves at once.</summary>
    public bool HidesImages
    {
        get => _settings.Shown.HidesImages;
        set => _ = _settings.SetHidesImagesAsync(value);
    }

    /// <summary>"Hide unwatched episode descriptions".</summary>
    public bool HidesOverviews
    {
        get => _settings.Shown.HidesOverviews;
        set => _ = _settings.SetHidesOverviewsAsync(value);
    }

    public bool IsLoading => _settings.State == EpisodeSpoilerState.Loading;

    /// <summary>The switches show: the profile's choice is known and the server keeps it.</summary>
    public bool IsReady => _settings.State == EpisodeSpoilerState.Ready;

    /// <summary>Why the switches cannot show; empty while loading and once they can.</summary>
    public string BlockerTitle => _settings.State switch
    {
        EpisodeSpoilerState.Unavailable => UnavailableTitle,
        EpisodeSpoilerState.Unsupported => UnsupportedTitle,
        _ => "",
    };

    public string BlockerText => _settings.State switch
    {
        EpisodeSpoilerState.Unavailable => UnavailableText,
        EpisodeSpoilerState.Unsupported => UnsupportedText,
        _ => "",
    };

    /// <summary>The Title group shows: the server keeps "Show title art" for the profile.</summary>
    public bool OffersTitleArt => Offers(TitlePageSettings.TitleArtKey);

    /// <summary>"Show title art"; saved at once, for this PC or, with <see cref="TitleArtForAllDevices"/>, for the profile.</summary>
    public bool ShowsTitleArt
    {
        get => _titlePage?.Shown.ShowsTitleArt ?? true;
        set => _ = _titlePage?.SetShowsTitleArtAsync(value);
    }

    /// <summary>"Apply to all devices".</summary>
    public bool TitleArtForAllDevices
    {
        get => _titlePage?.Shown.TitleArtForAllDevices ?? false;
        set => _ = _titlePage?.SetTitleArtForAllDevicesAsync(value);
    }

    /// <summary>The web's note under the switches: which devices the choice covers, or that the server's default applies.</summary>
    public string TitleArtNote => _titlePage?.Shown switch
    {
        { TitleArtForAllDevices: true, ShowsTitleArt: var on } =>
            $"Title art is {OnOff(on)} on every device signed into this profile. Changing it here changes it everywhere. Turn off “Apply to all devices” to choose per device again.",
        { TitleArtFollowsServerDefault: true, ShowsTitleArt: var on } when CanEditTitlePage =>
            $"Using the server default ({OnOff(on)}). Change it here to choose for this PC.",
        { TitleArtSource: Api.Settings.SettingScopes.ProfileDevice } => "Only affects this PC. Your other devices keep their own setting.",
        _ => "",
    };

    /// <summary>The note speaks for the profile's choice on every device, in the web's highlighted box.</summary>
    public bool TitleArtNoteIsBoxed => _titlePage?.Shown.TitleArtForAllDevices ?? false;

    /// <summary>The note shows in plain muted text.</summary>
    public bool ShowsTitleArtPlainNote => TitleArtNote.Length > 0 && !TitleArtNoteIsBoxed;

    /// <summary>"Use server default" shows: this PC or the profile chose.</summary>
    public bool CanUseServerDefaultTitleArt => CanEditTitlePage && _titlePage?.Shown.TitleArtFollowsServerDefault == false;

    /// <summary>Clears this PC's choice, and the profile's while it applies to all devices, so the server's default applies.</summary>
    [RelayCommand]
    private Task UseServerDefaultTitleArtAsync() => _titlePage?.UseServerDefaultTitleArtAsync() ?? Task.CompletedTask;

    /// <summary>The Sound group shows: the server keeps "Theme music" for the profile.</summary>
    public bool OffersThemeMusic => Offers(TitlePageSettings.ThemeMusicKey);

    /// <summary>"Loop theme music" shows.</summary>
    public bool OffersThemeMusicLoop => Offers(TitlePageSettings.ThemeMusicLoopKey);

    /// <summary>"Theme music"; saved at once for the profile.</summary>
    public bool PlaysThemeMusic
    {
        get => _titlePage?.Shown.PlaysThemeMusic ?? false;
        set => _ = _titlePage?.SetPlaysThemeMusicAsync(value);
    }

    /// <summary>"Loop theme music".</summary>
    public bool LoopsThemeMusic
    {
        get => _titlePage?.Shown.LoopsThemeMusic ?? false;
        set => _ = _titlePage?.SetLoopsThemeMusicAsync(value);
    }

    /// <summary>The Age guidance group shows: the server keeps "Show advisory age" for the profile.</summary>
    public bool OffersAdvisoryAge => Offers(TitlePageSettings.AdvisoryAgeKey);

    /// <summary>"Show advisory age"; the switch saves at once.</summary>
    public bool ShowsAdvisoryAge
    {
        get => _titlePage?.Shown.ShowsAdvisoryAge ?? false;
        set => _ = _titlePage?.SetShowsAdvisoryAgeAsync(value);
    }

    /// <summary>The Title Pages switches beside the spoiler ones take changes: the profile's choice is known.</summary>
    public bool CanEditTitlePage => _titlePage?.CanEdit ?? false;

    /// <summary>Why the last change did not stick, above the switches, which went back; empty when it did.</summary>
    public string ErrorMessage => _titlePage is { SaveFailed: true, OnlyTitleArtFailed: true } && !_settings.SaveFailed
        ? TitleArtSaveFailedText
        : _settings.SaveFailed || _titlePage?.SaveFailed == true ? SaveFailedText : "";

    /// <summary>Reads the switches again, as the page opens: another device may have changed them.</summary>
    public Task LoadAsync() => Task.WhenAll(_settings.RefreshAsync(), _titlePage?.RefreshAsync() ?? Task.CompletedTask);

    public void Dispose()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        _titlePage?.PropertyChanged -= OnTitlePageChanged;
    }

    private bool Offers(string key) => _titlePage?.Shown.Offered.Contains(key) ?? false;

    private static string OnOff(bool? on) => on == false ? "off" : "on";

    private void OnTitleArtNoteChanged()
    {
        OnPropertyChanged(nameof(TitleArtNote));
        OnPropertyChanged(nameof(TitleArtNoteIsBoxed));
        OnPropertyChanged(nameof(ShowsTitleArtPlainNote));
        OnPropertyChanged(nameof(CanUseServerDefaultTitleArt));
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EpisodeSpoilerSettings.Shown):
                OnPropertyChanged(nameof(HidesImages));
                OnPropertyChanged(nameof(HidesOverviews));
                break;
            case nameof(EpisodeSpoilerSettings.State):
                OnPropertyChanged(nameof(IsLoading));
                OnPropertyChanged(nameof(IsReady));
                OnPropertyChanged(nameof(BlockerTitle));
                OnPropertyChanged(nameof(BlockerText));
                break;
            case nameof(EpisodeSpoilerSettings.SaveFailed):
                OnPropertyChanged(nameof(ErrorMessage));
                break;
        }
    }

    private void OnTitlePageChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TitlePageSettings.Shown):
                OnPropertyChanged(nameof(OffersTitleArt));
                OnPropertyChanged(nameof(ShowsTitleArt));
                OnPropertyChanged(nameof(TitleArtForAllDevices));
                OnTitleArtNoteChanged();
                OnPropertyChanged(nameof(OffersThemeMusic));
                OnPropertyChanged(nameof(OffersThemeMusicLoop));
                OnPropertyChanged(nameof(PlaysThemeMusic));
                OnPropertyChanged(nameof(LoopsThemeMusic));
                OnPropertyChanged(nameof(OffersAdvisoryAge));
                OnPropertyChanged(nameof(ShowsAdvisoryAge));
                break;
            case nameof(TitlePageSettings.State):
                OnPropertyChanged(nameof(CanEditTitlePage));
                OnTitleArtNoteChanged();
                break;
            case nameof(TitlePageSettings.SaveFailed):
                OnPropertyChanged(nameof(ErrorMessage));
                break;
        }
    }
}
