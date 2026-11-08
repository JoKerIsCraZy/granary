using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Settings;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Title Pages, as Silo's web has it (<c>pages/settings/TitlePagesSettings.tsx</c>): its Spoilers group, the
/// profile's two switches (<see cref="EpisodeSpoilerSettings"/>), and its Age guidance (<see cref="TitlePageSettings"/>), each
/// saved at once for the profile. While the spoiler switches cannot be edited, the page says why instead; a group the
/// server does not keep is left out.
/// </summary>
public sealed partial class TitlePagesSettingsViewModel : ObservableObject, IDisposable
{
    public const string UnavailableTitle = "Couldn't load your spoiler settings";
    public const string UnavailableText = "Editing is off until they load, so nothing you chose earlier gets overwritten.";
    public const string UnsupportedTitle = "Your server needs an update";
    public const string UnsupportedText = "Spoiler settings can be changed once your server runs a newer version of Silo.";

    /// <summary>The web's words when a switch did not save.</summary>
    public const string SaveFailedText = "Failed to save title page setting";

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
    public string ErrorMessage => _settings.SaveFailed || _titlePage?.SaveFailed == true ? SaveFailedText : "";

    /// <summary>Reads the switches again, as the page opens: another device may have changed them.</summary>
    public Task LoadAsync() => Task.WhenAll(_settings.RefreshAsync(), _titlePage?.RefreshAsync() ?? Task.CompletedTask);

    public void Dispose()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        _titlePage?.PropertyChanged -= OnTitlePageChanged;
    }

    private bool Offers(string key) => _titlePage?.Shown.Offered.Contains(key) ?? false;

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
                OnPropertyChanged(nameof(OffersAdvisoryAge));
                OnPropertyChanged(nameof(ShowsAdvisoryAge));
                break;
            case nameof(TitlePageSettings.State):
                OnPropertyChanged(nameof(CanEditTitlePage));
                break;
            case nameof(TitlePageSettings.SaveFailed):
                OnPropertyChanged(nameof(ErrorMessage));
                break;
        }
    }
}
