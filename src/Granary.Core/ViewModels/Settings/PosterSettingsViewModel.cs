using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Cards;

namespace Granary.Core.ViewModels.Settings;

/// <summary>One of a few choices, as the web's ChoiceButtons: the chosen one is filled; Pick saves it.</summary>
public sealed partial class SettingsChoice(Func<string> label, Func<Task> pick) : ObservableObject
{
    public string Label => label();

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private Task PickAsync() => IsSelected ? Task.CompletedTask : pick();
}

/// <summary>
/// Settings → Navigation &amp; Cards → Posters, as Silo's web has it (pages/settings/InterfaceSettings.tsx, PosterSettings):
/// the size and what is written under each poster, saved for the desktop apps at once (<see cref="CardPresentationSettings"/>);
/// Reset while they have a choice of their own. While the choice cannot be edited, the page says why instead.
/// </summary>
public sealed partial class PosterSettingsViewModel : ObservableObject, IDisposable
{
    // Granary's own: the web's panel names the menu too, which Granary keeps on the PC.
    public static string UnavailableTitle => AppText.Get("granary.settings.posters.loadFailedTitle");

    public static string UnavailableText => AppText.Get("web.settings.interface.loadFailedDescription");

    public static string UnsupportedTitle => AppText.Get("web.settings.interface.unsupportedTitle");

    public static string UnsupportedText => AppText.Get("granary.settings.posters.unsupportedDescription");

    private static readonly (PosterSize Size, Func<string> Label)[] SizeLabels =
    [
        (PosterSize.Compact, () => AppText.Get("web.settings.interface.posterCompact")),
        (PosterSize.Standard, () => AppText.Get("web.settings.interface.posterStandard")),
        (PosterSize.Large, () => AppText.Get("web.settings.interface.posterLarge")),
    ];

    private static readonly (CardCaption Caption, Func<string> Label)[] CaptionLabels =
    [
        (CardCaption.TitleMetadata, () => AppText.Get("granary.settings.posters.captionTitleAndDetails")),
        (CardCaption.Title, () => AppText.Get("granary.settings.posters.captionTitleOnly")),
        (CardCaption.Artwork, () => AppText.Get("granary.settings.posters.captionNothing")),
    ];

    private readonly CardPresentationSettings _settings;

    public PosterSettingsViewModel(CardPresentationSettings settings)
    {
        _settings = settings;
        Sizes = [.. SizeLabels.Select(s => new SettingsChoice(s.Label, () => _settings.SetAsync(_settings.Shown with { PosterSize = s.Size })))];
        Captions = [.. CaptionLabels.Select(c => new SettingsChoice(c.Label, () => _settings.SetAsync(_settings.Shown with { Caption = c.Caption })))];
        _settings.PropertyChanged += OnSettingsChanged;
        ShowChoice();
    }

    /// <summary>Compact, Standard and Large; the same objects while the choice changes, so focus stays on the one picked.</summary>
    public IReadOnlyList<SettingsChoice> Sizes { get; }

    /// <summary>Title and details, Title only, Nothing.</summary>
    public IReadOnlyList<SettingsChoice> Captions { get; }

    public bool IsLoading => _settings.State == CardPresentationState.Loading;

    /// <summary>The choices show: the profile's choice is known and the server keeps it.</summary>
    public bool IsReady => _settings.State == CardPresentationState.Ready;

    /// <summary>The choices take a pick: not while this PC has its own value.</summary>
    public bool CanEdit => _settings.CanEdit;

    /// <summary>The desktop apps have their own choice; Reset shows only then, as on the web.</summary>
    public bool HasOverride => IsReady && _settings.HasOverride;

    public bool HasDeviceOverride => IsReady && _settings.HasDeviceOverride;

    /// <summary>Why the choices cannot show; empty while loading and once they can.</summary>
    public string BlockerTitle => _settings.State switch
    {
        CardPresentationState.Unavailable => UnavailableTitle,
        CardPresentationState.Unsupported => UnsupportedTitle,
        _ => "",
    };

    public string BlockerText => _settings.State switch
    {
        CardPresentationState.Unavailable => UnavailableText,
        CardPresentationState.Unsupported => UnsupportedText,
        _ => "",
    };

    /// <summary>Reads the choice again, as the page opens: another computer may have changed it.</summary>
    public Task LoadAsync() => _settings.RefreshAsync();

    [RelayCommand]
    private Task ResetAsync() => _settings.ResetAsync();

    /// <summary>Drops this PC's own value, the web's "Match my other browsers".</summary>
    [RelayCommand]
    private Task MatchOtherComputersAsync() => _settings.FollowOtherComputersAsync();

    public void Dispose() => _settings.PropertyChanged -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CardPresentationSettings.Shown):
                ShowChoice();
                break;
            case nameof(CardPresentationSettings.State):
                OnPropertyChanged(nameof(IsLoading));
                OnPropertyChanged(nameof(IsReady));
                OnPropertyChanged(nameof(BlockerTitle));
                OnPropertyChanged(nameof(BlockerText));
                OnPropertyChanged(nameof(HasOverride));
                OnPropertyChanged(nameof(HasDeviceOverride));
                break;
            case nameof(CardPresentationSettings.CanEdit):
                OnPropertyChanged(nameof(CanEdit));
                break;
            case nameof(CardPresentationSettings.HasOverride):
                OnPropertyChanged(nameof(HasOverride));
                break;
            case nameof(CardPresentationSettings.HasDeviceOverride):
                OnPropertyChanged(nameof(HasDeviceOverride));
                break;
        }
    }

    private void ShowChoice()
    {
        var shown = _settings.Shown;
        for (int i = 0; i < SizeLabels.Length; i++)
        {
            Sizes[i].IsSelected = SizeLabels[i].Size == shown.PosterSize;
        }
        for (int i = 0; i < CaptionLabels.Length; i++)
        {
            Captions[i].IsSelected = CaptionLabels[i].Caption == shown.Caption;
        }
    }
}
