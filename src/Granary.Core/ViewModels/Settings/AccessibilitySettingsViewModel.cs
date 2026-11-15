using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Settings;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Player;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Accessibility, as Silo's web has it (<c>pages/settings/AccessibilitySettings.tsx</c>), with its Language group:
/// the profile's app language (<see cref="AppLanguageSetting"/>), Match device and then each language Granary offers by its
/// own name, saved at once. Granary loads its texts as it starts, so the page says when a choice shows only after a
/// restart; the web switches at once.
/// </summary>
public sealed partial class AccessibilitySettingsViewModel : ObservableObject, IDisposable
{
    private const string MatchDevice = "";

    private readonly AppLanguageSetting _setting;
    private readonly IDeviceLanguages _device;
    private readonly IUiDispatcher _dispatcher;
    // What this run shows: the start chose its texts by the language kept then.
    private readonly (string Language, string Formats) _shown;

    public AccessibilitySettingsViewModel(AppLanguageSetting setting, AppLanguageFile file, IDeviceLanguages device, IUiDispatcher dispatcher)
    {
        _setting = setting;
        _device = device;
        _dispatcher = dispatcher;
        _shown = NextStart(file.AtStart);
        Choices = ChoicesFor(_setting.Stored);
        _setting.PropertyChanged += OnSettingChanged;
    }

    /// <summary>Match device, the profile's language when Granary does not offer it, then the languages it offers.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SettingChoice> Choices { get; private set; }

    /// <summary>The profile's language; choosing one saves it at once.</summary>
    public SettingChoice? SelectedChoice
    {
        get => Choices.FirstOrDefault(choice => choice.Value == (_setting.Stored ?? MatchDevice));
        set
        {
            if (value is null || value.Value == (_setting.Stored ?? MatchDevice))
            {
                return;
            }
            // After the select's own change: a choice can change the list, which the select must not see mid-change.
            _dispatcher.Post(() =>
            {
                Working = _setting.ChooseAsync(value.Value.Length == 0 ? null : value.Value);
                if (SelectedChoice?.Value != value.Value)
                {
                    // Not taken, as one is on its way (the web's one save at a time): the select goes back.
                    OnPropertyChanged(nameof(SelectedChoice));
                }
            });
        }
    }

    /// <summary>
    /// The select can be used: the profile's language is known. It stays on while a choice saves, as the web's, so it keeps
    /// the keyboard; another choice meanwhile is not taken.
    /// </summary>
    public bool CanChoose => _setting.State == AppLanguageState.Ready;

    /// <summary>The Language group shows: the server keeps the setting, or has not said yet.</summary>
    public bool OffersLanguage => _setting.State != AppLanguageState.Unsupported;

    /// <summary>The server predates the setting: the page says why there is nothing to choose.</summary>
    public bool IsUnsupported => _setting.State == AppLanguageState.Unsupported;

    /// <summary>The profile's language could not be read; Retry reads it again.</summary>
    public bool LoadFailed => _setting.State == AppLanguageState.Unavailable;

    /// <summary>
    /// Under the select when the profile's language is one Granary does not offer, as the web's note for a language it lacks;
    /// empty otherwise.
    /// </summary>
    public string UnavailableNote => _setting.Stored is { } stored && !Offered(stored)
        ? AppText.Get("granary.settings.accessibility.appLanguageUnavailable")
        : "";

    /// <summary>
    /// Under the select when the next start shows another language, or other dates and numbers, than this run: Granary loads
    /// its texts as it starts. It follows what is kept for the next start, so a choice that does not apply here yet (its
    /// save on its way, or this PC's own value not cleared) says nothing of a restart. Empty otherwise.
    /// </summary>
    public string RestartNote => NextStart(_setting.KeptForNextStart) != _shown
        ? AppText.Get("web.admin.backups.restore.caveat.restart")
        : "";

    /// <summary>What went wrong with the last choice, in the web's words; empty otherwise.</summary>
    public string FailureText => _setting.Failure switch
    {
        AppLanguageFailure.NotSaved => AppText.Get("web.settings.accessibility.appLanguageSaveFailed"),
        AppLanguageFailure.DeviceNotCleared => AppText.Get("granary.settings.accessibility.appLanguageDeviceClearFailed"),
        _ => "",
    };

    /// <summary>Try again shows: the choice was saved, but clearing this PC's own value failed.</summary>
    public bool OffersTryAgain => _setting.Failure == AppLanguageFailure.DeviceNotCleared;

    /// <summary>The read or choice in flight, for tests.</summary>
    internal Task Working { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the profile's language again as the page opens: another device may have changed it.</summary>
    public Task LoadAsync() => Working = _setting.RefreshAsync();

    public void Dispose() => _setting.PropertyChanged -= OnSettingChanged;

    [RelayCommand]
    private Task TryAgainAsync() => Working = RetryThenReadAsync();

    // A clear that lands reads the profile's language again: both are the one step Try again takes.
    private async Task RetryThenReadAsync()
    {
        await _setting.RetryDeviceClearAsync();
        await _setting.Working;
    }

    [RelayCommand]
    private Task RetryAsync() => LoadAsync();

    // A stored tag counts as offered only as written, as the web's picker compares it.
    private static bool Offered(string tag) => AppLanguage.Enabled.Contains(tag, StringComparer.Ordinal);

    // A stored tag Granary does not offer stays listed under its name, so saving here never clears another app's choice.
    private static IReadOnlyList<SettingChoice> ChoicesFor(string? stored)
    {
        var offered = AppLanguage.Enabled.Select(tag => new SettingChoice(tag, AppLanguage.Endonym(tag) ?? tag));
        SettingChoice matchDevice = new(MatchDevice, AppText.Get("web.settings.accessibility.matchDevice"));
        return stored is null || Offered(stored)
            ? [matchDevice, .. offered]
            : [matchDevice, new SettingChoice(stored, AppLanguage.Endonym(stored) ?? TrackText.LanguageName(stored)), .. offered];
    }

    private (string Language, string Formats) NextStart(string? stored)
    {
        var (language, formats) = AppLanguage.ForStart(stored, _device, AppLanguage.Enabled);
        return (language, formats.Name);
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppLanguageSetting.Stored):
                var choices = ChoicesFor(_setting.Stored);
                if (!choices.SequenceEqual(Choices))
                {
                    Choices = choices;
                }
                OnPropertyChanged(nameof(SelectedChoice));
                OnPropertyChanged(nameof(UnavailableNote));
                break;
            case nameof(AppLanguageSetting.State):
                OnPropertyChanged(nameof(CanChoose));
                OnPropertyChanged(nameof(OffersLanguage));
                OnPropertyChanged(nameof(IsUnsupported));
                OnPropertyChanged(nameof(LoadFailed));
                break;
            case nameof(AppLanguageSetting.KeptForNextStart):
                OnPropertyChanged(nameof(RestartNote));
                break;
            case nameof(AppLanguageSetting.Failure):
                OnPropertyChanged(nameof(FailureText));
                OnPropertyChanged(nameof(OffersTryAgain));
                break;
        }
    }
}
