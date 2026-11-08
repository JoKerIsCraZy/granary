using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Settings;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Title Pages, as Silo's web has it (<c>pages/settings/TitlePagesSettings.tsx</c>), with its Spoilers group: the
/// profile's two switches (<see cref="EpisodeSpoilerSettings"/>), each saved at once for the profile. While they cannot be
/// edited, the page says why instead.
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

    public TitlePagesSettingsViewModel(EpisodeSpoilerSettings settings)
    {
        _settings = settings;
        _settings.PropertyChanged += OnSettingsChanged;
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

    /// <summary>Why the last change did not stick, above the switches, which went back; empty when it did.</summary>
    public string ErrorMessage => _settings.SaveFailed ? SaveFailedText : "";

    /// <summary>Reads the switches again, as the page opens: another device may have changed them.</summary>
    public Task LoadAsync() => _settings.RefreshAsync();

    public void Dispose() => _settings.PropertyChanged -= OnSettingsChanged;

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
}
