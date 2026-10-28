using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.ViewModels.Cards;

namespace Granary.Core.ViewModels.Settings;

/// <summary>One of a few choices, as the web's ChoiceButtons: the chosen one is filled; Pick saves it.</summary>
public sealed partial class SettingsChoice(string label, Func<Task> pick) : ObservableObject
{
    public string Label { get; } = label;

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
    public const string UnavailableTitle = "Couldn't load your poster settings";
    public const string UnavailableText = "Editing is off until they load, so nothing you chose earlier gets overwritten.";
    public const string UnsupportedTitle = "Your server needs an update";
    public const string UnsupportedText = "Poster size and captions can be changed once your server runs a newer version of Silo.";

    private static readonly (PosterSize Size, string Label)[] SizeLabels =
        [(PosterSize.Compact, "Compact"), (PosterSize.Standard, "Standard"), (PosterSize.Large, "Large")];

    private static readonly (CardCaption Caption, string Label)[] CaptionLabels =
        [(CardCaption.TitleMetadata, "Title and details"), (CardCaption.Title, "Title only"), (CardCaption.Artwork, "Nothing")];

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
