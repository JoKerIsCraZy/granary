using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Settings;
using Granary.Core.Playback;
using Granary.Core.Settings;
using Granary.Core.ViewModels.Player;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Language &amp; Subtitles, as Silo's web has it (pages/settings/SubtitleAppearanceSettings.tsx): the spoken
/// and subtitle languages, when subtitles show, how they look and their timing. Languages and behaviour are saved at
/// once; the look (<see cref="Look"/>) is a draft with Save and Discard, and Reset brings back the default everywhere;
/// the offset is saved with each step.
/// </summary>
public sealed partial class LanguageSettingsViewModel : ObservableObject
{
    public const int OffsetStepMs = 100;
    public const int MaxOffsetMs = PlaybackPreferencesReader.MaxSubtitleDelayMs;

    private static readonly string[] Keys =
    [
        SettingKeys.AudioLanguage, SettingKeys.SubtitleLanguage, SettingKeys.SubtitleMode, SettingKeys.ShowForcedSubtitles,
        SettingKeys.SubtitleAppearance, SettingKeys.SubtitleSyncMs,
    ];

    // The server's playback language list (settings manifest option sets playback_audio_languages, _subtitle_languages).
    private static readonly string[] Languages =
    [
        "ar", "bn", "bg", "zh", "zh-Hans", "zh-Hant", "hr", "cs", "da", "nl", "en", "en-US", "en-GB", "fi", "fr", "fr-CA", "de", "el", "he",
        "hi", "hu", "id", "it", "ja", "ko", "ms", "no", "fa", "pl", "pt", "pt-BR", "pt-PT", "ro", "ru", "sk", "sl", "es", "es-419", "es-ES",
        "sv", "ta", "te", "th", "tr", "uk", "vi",
    ];

    private readonly IProfileSettings _settings;
    private readonly ILogger<LanguageSettingsViewModel> _logger;
    private readonly SettingSaves _saves;
    private readonly Dictionary<string, string?> _sources = [];

    // What the server holds for each control, as far as this page knows: a refused save goes back to it.
    private readonly Dictionary<string, object?> _confirmed = [];
    private SubtitleAppearance _saved = SubtitleAppearanceEditor.WebDefault;
    private bool _applying;

    public LanguageSettingsViewModel(IProfileSettings settings, ILogger<LanguageSettingsViewModel> logger)
    {
        _settings = settings;
        _logger = logger;
        _saves = new SettingSaves(logger);
        Look.Edited += (_, _) => OnLookChanged();
    }

    /// <summary>The look being edited, shown in the preview; saved with <see cref="SaveAppearanceCommand"/>.</summary>
    public SubtitleAppearanceEditor Look { get; } = new();

    [ObservableProperty]
    public partial IReadOnlyList<SettingChoice> SpokenLanguageChoices { get; private set; } = LanguageChoices("No preference");

    [ObservableProperty]
    public partial IReadOnlyList<SettingChoice> SubtitleLanguageChoices { get; private set; } = LanguageChoices("None");

    public IReadOnlyList<SettingChoice> SubtitleModeChoices { get; } = [new("auto", "Auto"), new("always", "Always on"), new("off", "Off")];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; } = true;

    [ObservableProperty]
    public partial string ErrorMessage { get; private set; } = "";

    [ObservableProperty]
    public partial SettingChoice? SelectedSpokenLanguage { get; set; }

    [ObservableProperty]
    public partial SettingChoice? SelectedSubtitleLanguage { get; set; }

    [ObservableProperty]
    public partial SettingChoice? SelectedSubtitleMode { get; set; }

    [ObservableProperty]
    public partial bool ShowForcedSubtitles { get; set; } = true;

    /// <summary>A stored look other than the default: Reset has something to do.</summary>
    [ObservableProperty]
    public partial bool IsAppearanceCustomized { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffsetText), nameof(CanOffsetEarlier), nameof(CanOffsetLater), nameof(CanResetOffset))]
    public partial int OffsetMs { get; private set; }

    /// <summary>"+300 ms", "0 ms", "−200 ms".</summary>
    public string OffsetText => OffsetMs == 0 ? "0 ms" : (OffsetMs > 0 ? "+" : "−") + Math.Abs(OffsetMs).ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms";

    public bool CanOffsetEarlier => OffsetMs > -MaxOffsetMs;

    public bool CanOffsetLater => OffsetMs < MaxOffsetMs;

    public bool CanResetOffset => OffsetMs != 0;

    public bool IsAppearanceChanged => Look.Appearance != _saved;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorMessage = "";
        try
        {
            Apply(await _settings.ReadAsync(Keys, cancellationToken));
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken) || ex is InvalidOperationException)
        {
            _logger.LogInformation("The language and subtitle settings could not be read: {Failure}", ApiFailureText.Describe(ex));
            ErrorMessage = "Could not load the language and subtitle settings. " + ApiFailureText.Describe(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsAppearanceChanged))]
    private Task SaveAppearanceAsync()
    {
        var appearance = Look.Appearance;
        bool clearDevice = _sources.GetValueOrDefault(SettingKeys.SubtitleAppearance) == SettingScopes.ProfileDevice;
        ErrorMessage = "";
        return _saves.RunAsync(() => _settings.SaveAsync(SettingKeys.SubtitleAppearance, appearance.ToJson(), clearDevice, CancellationToken.None), saved =>
        {
            if (!saved)
            {
                ErrorMessage = "Failed to save subtitle appearance";
                return;
            }
            _sources[SettingKeys.SubtitleAppearance] = SettingScopes.Profile;
            _saved = appearance;
            IsAppearanceCustomized = true;
            OnLookChanged();
        });
    }

    [RelayCommand(CanExecute = nameof(IsAppearanceChanged))]
    private void DiscardAppearance()
    {
        Look.Show(_saved);
        OnLookChanged();
    }

    /// <summary>The profile's look and this device's are cleared, so the default applies on every device again; edits go with it.</summary>
    [RelayCommand]
    private Task ResetAppearanceAsync()
    {
        ErrorMessage = "";
        return _saves.RunAsync(() => _settings.ResetAsync(SettingKeys.SubtitleAppearance, CancellationToken.None), saved =>
        {
            if (!saved)
            {
                ErrorMessage = "Failed to reset subtitle appearance";
                return;
            }
            _sources[SettingKeys.SubtitleAppearance] = "default";
            _saved = SubtitleAppearanceEditor.WebDefault;
            IsAppearanceCustomized = false;
            Look.Show(_saved);
            OnLookChanged();
        });
    }

    [RelayCommand]
    private Task OffsetEarlierAsync() => SaveOffsetAsync(OffsetMs - OffsetStepMs);

    [RelayCommand]
    private Task OffsetLaterAsync() => SaveOffsetAsync(OffsetMs + OffsetStepMs);

    [RelayCommand]
    private Task ResetOffsetAsync() => SaveOffsetAsync(0);

    private static IReadOnlyList<SettingChoice> LanguageChoices(string none) =>
        [new SettingChoice("", none), .. Languages.Select(code => new SettingChoice(code, TrackText.LanguageName(code))).OrderBy(c => c.Label, StringComparer.CurrentCulture)];

    private void OnLookChanged()
    {
        OnPropertyChanged(nameof(IsAppearanceChanged));
        SaveAppearanceCommand.NotifyCanExecuteChanged();
        DiscardAppearanceCommand.NotifyCanExecuteChanged();
    }

    private void Apply(IReadOnlyDictionary<string, EffectiveSetting> values)
    {
        _applying = true;
        try
        {
            foreach (var (key, value) in values)
            {
                _sources[key] = value.Source;
            }
            EffectiveSetting? Get(string key) => values.GetValueOrDefault(key);
            (SpokenLanguageChoices, var spoken) = Language(SpokenLanguageChoices, SettingValues.Text(Get(SettingKeys.AudioLanguage)));
            SelectedSpokenLanguage = spoken;
            (SubtitleLanguageChoices, var subtitle) = Language(SubtitleLanguageChoices, SettingValues.Text(Get(SettingKeys.SubtitleLanguage)));
            SelectedSubtitleLanguage = subtitle;
            SelectedSubtitleMode = SubtitleModeChoices.FirstOrDefault(c => c.Value == SettingValues.Text(Get(SettingKeys.SubtitleMode))) ?? SubtitleModeChoices[0];
            ShowForcedSubtitles = SettingValues.Bool(Get(SettingKeys.ShowForcedSubtitles)) ?? true;
            _saved = Get(SettingKeys.SubtitleAppearance) is { } stored ? SubtitleAppearance.From(stored.Value) : SubtitleAppearanceEditor.WebDefault;
            IsAppearanceCustomized = Get(SettingKeys.SubtitleAppearance)?.Source is { } source && source != "default";
            Look.Show(_saved);
            OnLookChanged();
            OffsetMs = SettingValues.Number(Get(SettingKeys.SubtitleSyncMs)) is { } ms ? (int)Math.Clamp(Math.Round(ms), -MaxOffsetMs, MaxOffsetMs) : 0;
            _confirmed[SettingKeys.AudioLanguage] = SelectedSpokenLanguage;
            _confirmed[SettingKeys.SubtitleLanguage] = SelectedSubtitleLanguage;
            _confirmed[SettingKeys.SubtitleMode] = SelectedSubtitleMode;
            _confirmed[SettingKeys.ShowForcedSubtitles] = ShowForcedSubtitles;
            _confirmed[SettingKeys.SubtitleSyncMs] = OffsetMs;
        }
        finally
        {
            _applying = false;
        }
    }

    // A tag the list does not have (another client's) joins it, so the select shows it as itself.
    private static (IReadOnlyList<SettingChoice> Choices, SettingChoice Selected) Language(IReadOnlyList<SettingChoice> choices, string? tag)
    {
        if (tag is null)
        {
            return (choices, choices[0]);
        }
        if (choices.FirstOrDefault(c => string.Equals(c.Value, tag, StringComparison.OrdinalIgnoreCase)) is { } known)
        {
            return (choices, known);
        }
        var added = new SettingChoice(tag, TrackText.LanguageName(tag));
        return ([choices[0], .. choices.Skip(1).Append(added).OrderBy(c => c.Label, StringComparer.CurrentCulture)], added);
    }

    // "No preference" and "None" clear the profile's value (and this device's), so the setting inherits again.
    partial void OnSelectedSpokenLanguageChanged(SettingChoice? value)
    {
        if (value is not null)
        {
            Save(SettingKeys.AudioLanguage, value, () => SelectedSpokenLanguage, v => SelectedSpokenLanguage = v,
                value.Value.Length == 0 ? null : SettingValues.Json(value.Value), "Failed to save language");
        }
    }

    partial void OnSelectedSubtitleLanguageChanged(SettingChoice? value)
    {
        if (value is not null)
        {
            Save(SettingKeys.SubtitleLanguage, value, () => SelectedSubtitleLanguage, v => SelectedSubtitleLanguage = v,
                value.Value.Length == 0 ? null : SettingValues.Json(value.Value), "Failed to save language");
        }
    }

    partial void OnSelectedSubtitleModeChanged(SettingChoice? value)
    {
        if (value is not null)
        {
            Save(SettingKeys.SubtitleMode, value, () => SelectedSubtitleMode, v => SelectedSubtitleMode = v, SettingValues.Json(value.Value));
        }
    }

    partial void OnShowForcedSubtitlesChanged(bool value) =>
        Save(SettingKeys.ShowForcedSubtitles, value, () => ShowForcedSubtitles, v => ShowForcedSubtitles = v, SettingValues.Json(value));

    private Task SaveOffsetAsync(int offsetMs)
    {
        int offset = Math.Clamp(offsetMs, -MaxOffsetMs, MaxOffsetMs);
        _applying = true;
        try
        {
            OffsetMs = offset;
        }
        finally
        {
            _applying = false;
        }
        return Save(SettingKeys.SubtitleSyncMs, offset, () => OffsetMs, v => OffsetMs = v, SettingValues.Json(offset), "Failed to save subtitle offset");
    }

    // Saved after the saves before it; a refusal puts the control back to what the server holds, unless the viewer
    // has moved it on since. A null value is a reset.
    private Task Save<T>(string key, T value, Func<T> shown, Action<T> show, JsonElement? json, string failure = "Failed to save subtitle setting")
    {
        if (_applying)
        {
            return Task.CompletedTask;
        }
        bool clearDevice = _sources.GetValueOrDefault(key) == SettingScopes.ProfileDevice;
        ErrorMessage = "";
        return _saves.RunAsync(
            () => json is { } stored ? _settings.SaveAsync(key, stored, clearDevice, CancellationToken.None) : _settings.ResetAsync(key, CancellationToken.None),
            saved =>
            {
                if (saved)
                {
                    _confirmed[key] = value;
                    _sources[key] = SettingScopes.Profile;
                    return;
                }
                ErrorMessage = failure;
                if (EqualityComparer<T>.Default.Equals(shown(), value) && _confirmed.GetValueOrDefault(key) is T confirmed)
                {
                    _applying = true;
                    try
                    {
                        show(confirmed);
                    }
                    finally
                    {
                        _applying = false;
                    }
                }
            });
    }
}
