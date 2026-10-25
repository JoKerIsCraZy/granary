using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Settings;
using Granary.Core.Playback;
using Granary.Core.Settings;
using Granary.Core.ViewModels.Player;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Settings;

/// <summary>A colour of the appearance palettes: its value, its name, and whether it is the one chosen.</summary>
public sealed record ColorSwatch(string Hex, string Label, bool IsSelected);

/// <summary>
/// Settings → Language &amp; Subtitles, as Silo's web has it (pages/settings/SubtitleAppearanceSettings.tsx): the spoken
/// and subtitle languages, when subtitles show, how they look and their timing. Languages and behaviour are saved at
/// once; the look is a draft with Save and Discard, and Reset brings back the default everywhere; the offset is saved
/// with each step.
/// </summary>
public sealed partial class LanguageSettingsViewModel(IProfileSettings settings, ILogger<LanguageSettingsViewModel> logger) : ObservableObject
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

    // web/src/lib/subtitleAppearance.ts.
    private static readonly (string Hex, string Label)[] FontColors =
    [
        ("#ffffff", "White"), ("#facc15", "Yellow"), ("#22c55e", "Green"), ("#06b6d4", "Cyan"), ("#d946ef", "Magenta"), ("#ef4444", "Red"),
        ("#3b82f6", "Blue"), ("#9ca3af", "Gray"), ("#000000", "Black"),
    ];

    private static readonly (string Hex, string Label)[] BackgroundColors =
        [("#000000", "Black"), ("#374151", "Dark Gray"), ("#1e3a5f", "Navy"), ("#7f1d1d", "Dark Red"), ("#14532d", "Dark Green")];

    private readonly Dictionary<string, string?> _sources = [];
    private SubtitleAppearance _saved = SubtitleAppearance.Default;
    private bool _applying;

    public IReadOnlyList<SettingChoice> SpokenLanguageChoices { get; } = LanguageChoices("No preference");

    public IReadOnlyList<SettingChoice> SubtitleLanguageChoices { get; } = LanguageChoices("None");

    public IReadOnlyList<SettingChoice> SubtitleModeChoices { get; } = [new("auto", "Auto"), new("always", "Always on"), new("off", "Off")];

    public IReadOnlyList<SettingChoice> FontSizeChoices { get; } =
        [new("small", "Small"), new("medium", "Medium"), new("large", "Large"), new("xlarge", "X-Large"), new("xxlarge", "XX-Large")];

    public IReadOnlyList<SettingChoice> FontFamilyChoices { get; } =
        [new(SubtitleAppearance.SansSerif, "Sans-serif"), new(SubtitleAppearance.Serif, "Serif"), new(SubtitleAppearance.Monospace, "Monospace")];

    public IReadOnlyList<SettingChoice> BackgroundStyleChoices { get; } =
        [new("box", "Box"), new("shadow", "Drop Shadow"), new("outline", "Outline"), new("none", "None")];

    public IReadOnlyList<SettingChoice> PositionChoices { get; } = [new("bottom", "Bottom"), new("lower-third", "Lower Third"), new("top", "Top")];

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

    /// <summary>The look being edited, shown in the preview; saved with <see cref="SaveAppearanceCommand"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppearanceChanged))]
    [NotifyCanExecuteChangedFor(nameof(SaveAppearanceCommand), nameof(DiscardAppearanceCommand))]
    public partial SubtitleAppearance Appearance { get; private set; } = SubtitleAppearance.Default;

    /// <summary>A stored look other than the default: Reset has something to do.</summary>
    [ObservableProperty]
    public partial bool IsAppearanceCustomized { get; private set; }

    [ObservableProperty]
    public partial SettingChoice? SelectedFontSize { get; set; }

    [ObservableProperty]
    public partial SettingChoice? SelectedFontFamily { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ColorSwatch> FontColorSwatches { get; private set; } = [];

    [ObservableProperty]
    public partial double TextOpacity { get; set; } = 100;

    [ObservableProperty]
    public partial bool TextOutline { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ColorSwatch> OutlineColorSwatches { get; private set; } = [];

    [ObservableProperty]
    public partial SettingChoice? SelectedBackgroundStyle { get; set; }

    [ObservableProperty]
    public partial double BackgroundOpacity { get; set; } = 75;

    [ObservableProperty]
    public partial bool IsBox { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ColorSwatch> BackgroundColorSwatches { get; private set; } = [];

    [ObservableProperty]
    public partial SettingChoice? SelectedPosition { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffsetText), nameof(CanOffsetEarlier), nameof(CanOffsetLater), nameof(CanResetOffset))]
    public partial int OffsetMs { get; private set; }

    /// <summary>"+300 ms", "0 ms", "−200 ms".</summary>
    public string OffsetText => OffsetMs == 0 ? "0 ms" : (OffsetMs > 0 ? "+" : "−") + Math.Abs(OffsetMs).ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms";

    public bool CanOffsetEarlier => OffsetMs > -MaxOffsetMs;

    public bool CanOffsetLater => OffsetMs < MaxOffsetMs;

    public bool CanResetOffset => OffsetMs != 0;

    public bool IsAppearanceChanged => Appearance != _saved;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorMessage = "";
        try
        {
            Apply(await settings.ReadAsync(Keys, cancellationToken));
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            logger.LogInformation("The language and subtitle settings could not be read: {Failure}", ApiFailureText.Describe(ex));
            ErrorMessage = "Could not load the language and subtitle settings. " + ApiFailureText.Describe(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>A swatch of the font colours was picked.</summary>
    public void PickFontColor(string hex) => Edit(Appearance with { FontColor = hex });

    public void PickOutlineColor(string hex) => Edit(Appearance with { TextOutlineColor = hex });

    public void PickBackgroundColor(string hex) => Edit(Appearance with { BackgroundColor = hex });

    [RelayCommand(CanExecute = nameof(IsAppearanceChanged))]
    private async Task SaveAppearanceAsync()
    {
        var appearance = Appearance;
        bool clearDevice = _sources.GetValueOrDefault(SettingKeys.SubtitleAppearance) == SettingScopes.ProfileDevice;
        if (await TryAsync(() => settings.SaveAsync(SettingKeys.SubtitleAppearance, appearance.ToJson(), clearDevice, CancellationToken.None),
            "Failed to save subtitle appearance"))
        {
            _sources[SettingKeys.SubtitleAppearance] = SettingScopes.Profile;
            _saved = appearance;
            IsAppearanceCustomized = true;
            OnPropertyChanged(nameof(IsAppearanceChanged));
            SaveAppearanceCommand.NotifyCanExecuteChanged();
            DiscardAppearanceCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(IsAppearanceChanged))]
    private void DiscardAppearance() => ShowAppearance(_saved);

    /// <summary>The profile's look and this device's are cleared, so the default applies on every device again; edits go with it.</summary>
    [RelayCommand]
    private async Task ResetAppearanceAsync()
    {
        if (await TryAsync(() => settings.ResetAsync(SettingKeys.SubtitleAppearance, CancellationToken.None), "Failed to reset subtitle appearance"))
        {
            _sources[SettingKeys.SubtitleAppearance] = "default";
            _saved = WebDefault;
            IsAppearanceCustomized = false;
            ShowAppearance(WebDefault);
        }
    }

    [RelayCommand]
    private Task OffsetEarlierAsync() => SaveOffsetAsync(OffsetMs - OffsetStepMs);

    [RelayCommand]
    private Task OffsetLaterAsync() => SaveOffsetAsync(OffsetMs + OffsetStepMs);

    [RelayCommand]
    private Task ResetOffsetAsync() => SaveOffsetAsync(0);

    // The contract's default look (settings manifest): a box, where the apps' built-in fallback has a shadow.
    private static SubtitleAppearance WebDefault { get; } = SubtitleAppearance.Default with { Background = SubtitleBackground.Box };

    private static IReadOnlyList<SettingChoice> LanguageChoices(string none) =>
        [new SettingChoice("", none), .. Languages.Select(code => new SettingChoice(code, TrackText.LanguageName(code))).OrderBy(c => c.Label, StringComparer.CurrentCulture)];

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
            SelectedSpokenLanguage = Language(SpokenLanguageChoices, SettingValues.Text(Get(SettingKeys.AudioLanguage)));
            SelectedSubtitleLanguage = Language(SubtitleLanguageChoices, SettingValues.Text(Get(SettingKeys.SubtitleLanguage)));
            SelectedSubtitleMode = SubtitleModeChoices.FirstOrDefault(c => c.Value == SettingValues.Text(Get(SettingKeys.SubtitleMode))) ?? SubtitleModeChoices[0];
            ShowForcedSubtitles = SettingValues.Bool(Get(SettingKeys.ShowForcedSubtitles)) ?? true;
            var appearance = Get(SettingKeys.SubtitleAppearance) is { } stored ? SubtitleAppearance.From(stored.Value) : WebDefault;
            IsAppearanceCustomized = Get(SettingKeys.SubtitleAppearance)?.Source is { } source && source != "default";
            _saved = appearance;
            ShowAppearance(appearance);
            OffsetMs = SettingValues.Number(Get(SettingKeys.SubtitleSyncMs)) is { } ms ? (int)Math.Clamp(Math.Round(ms), -MaxOffsetMs, MaxOffsetMs) : 0;
        }
        finally
        {
            _applying = false;
        }
    }

    // A tag the list does not have (another client's) still reads as itself.
    private static SettingChoice Language(IReadOnlyList<SettingChoice> choices, string? tag) =>
        tag is null ? choices[0]
            : choices.FirstOrDefault(c => string.Equals(c.Value, tag, StringComparison.OrdinalIgnoreCase)) ?? new SettingChoice(tag, TrackText.LanguageName(tag));

    private void ShowAppearance(SubtitleAppearance appearance)
    {
        bool applying = _applying;
        _applying = true;
        try
        {
            Appearance = appearance;
            SelectedFontSize = FontSizeChoices.First(c => c.Value == SubtitleAppearance.SizeName(appearance.FontSize));
            SelectedFontFamily = FontFamilyChoices.FirstOrDefault(c => c.Value == appearance.FontFamily) ?? FontFamilyChoices[0];
            TextOpacity = appearance.TextOpacity;
            TextOutline = appearance.TextOutline;
            SelectedBackgroundStyle = BackgroundStyleChoices.First(c => c.Value == SubtitleAppearance.BackgroundName(appearance.Background));
            BackgroundOpacity = appearance.BackgroundOpacity;
            IsBox = appearance.Background == SubtitleBackground.Box;
            SelectedPosition = PositionChoices.First(c => c.Value == SubtitleAppearance.PositionName(appearance.Position));
            FontColorSwatches = Swatches(FontColors, appearance.FontColor);
            OutlineColorSwatches = Swatches(FontColors, appearance.TextOutlineColor);
            BackgroundColorSwatches = Swatches(BackgroundColors, appearance.BackgroundColor);
        }
        finally
        {
            _applying = applying;
        }
    }

    private static IReadOnlyList<ColorSwatch> Swatches((string Hex, string Label)[] palette, string selected) =>
        [.. palette.Select(c => new ColorSwatch(c.Hex, c.Label, string.Equals(c.Hex, selected, StringComparison.OrdinalIgnoreCase)))];

    private void Edit(SubtitleAppearance appearance)
    {
        if (!_applying)
        {
            ShowAppearance(appearance);
        }
    }

    partial void OnSelectedFontSizeChanged(SettingChoice? value)
    {
        if (value is not null)
        {
            Edit(Appearance with { FontSize = SubtitleAppearance.SizeOf(value.Value) });
        }
    }

    partial void OnSelectedFontFamilyChanged(SettingChoice? value)
    {
        if (value is not null)
        {
            Edit(Appearance with { FontFamily = value.Value });
        }
    }

    partial void OnTextOpacityChanged(double value)
    {
        if (double.IsFinite(value))
        {
            Edit(Appearance with { TextOpacity = (int)Math.Clamp(Math.Round(value), 1, 100) });
        }
    }

    partial void OnTextOutlineChanged(bool value) => Edit(Appearance with { TextOutline = value });

    partial void OnSelectedBackgroundStyleChanged(SettingChoice? value)
    {
        if (value is not null)
        {
            Edit(Appearance with
            {
                Background = value.Value switch
                {
                    "box" => SubtitleBackground.Box,
                    "outline" => SubtitleBackground.Outline,
                    "none" => SubtitleBackground.None,
                    _ => SubtitleBackground.Shadow,
                },
            });
        }
    }

    partial void OnBackgroundOpacityChanged(double value)
    {
        if (double.IsFinite(value))
        {
            Edit(Appearance with { BackgroundOpacity = (int)Math.Clamp(Math.Round(value), 0, 100) });
        }
    }

    partial void OnSelectedPositionChanged(SettingChoice? value)
    {
        if (value is not null)
        {
            Edit(Appearance with
            {
                Position = value.Value switch
                {
                    "lower-third" => SubtitlePosition.LowerThird,
                    "top" => SubtitlePosition.Top,
                    _ => SubtitlePosition.Bottom,
                },
            });
        }
    }

    // "No preference" and "None" clear the profile's value (and this device's), so the setting inherits again.
    partial void OnSelectedSpokenLanguageChanged(SettingChoice? oldValue, SettingChoice? newValue) =>
        SaveLanguage(SettingKeys.AudioLanguage, newValue, () => SelectedSpokenLanguage = oldValue);

    partial void OnSelectedSubtitleLanguageChanged(SettingChoice? oldValue, SettingChoice? newValue) =>
        SaveLanguage(SettingKeys.SubtitleLanguage, newValue, () => SelectedSubtitleLanguage = oldValue);

    partial void OnSelectedSubtitleModeChanged(SettingChoice? oldValue, SettingChoice? newValue)
    {
        if (!_applying && newValue is not null)
        {
            _ = SaveAsync(SettingKeys.SubtitleMode, SettingValues.Json(newValue.Value), () => SelectedSubtitleMode = oldValue);
        }
    }

    partial void OnShowForcedSubtitlesChanged(bool value)
    {
        if (!_applying)
        {
            _ = SaveAsync(SettingKeys.ShowForcedSubtitles, SettingValues.Json(value), () => ShowForcedSubtitles = !value);
        }
    }

    private void SaveLanguage(string key, SettingChoice? choice, Action revert)
    {
        if (_applying || choice is null)
        {
            return;
        }
        if (choice.Value.Length == 0)
        {
            _ = WriteAsync(key, () => settings.ResetAsync(key, CancellationToken.None), revert, "Failed to save language");
            return;
        }
        _ = SaveAsync(key, SettingValues.Json(choice.Value), revert, "Failed to save language");
    }

    private async Task SaveOffsetAsync(int offsetMs)
    {
        int previous = OffsetMs;
        OffsetMs = Math.Clamp(offsetMs, -MaxOffsetMs, MaxOffsetMs);
        await SaveAsync(SettingKeys.SubtitleSyncMs, SettingValues.Json(OffsetMs), () => OffsetMs = previous, "Failed to save subtitle offset");
    }

    private Task SaveAsync(string key, JsonElement value, Action revert, string failure = "Failed to save subtitle setting")
    {
        bool clearDevice = _sources.GetValueOrDefault(key) == SettingScopes.ProfileDevice;
        return WriteAsync(key, () => settings.SaveAsync(key, value, clearDevice, CancellationToken.None), revert, failure);
    }

    private async Task WriteAsync(string key, Func<Task> write, Action revert, string failure)
    {
        if (await TryAsync(write, failure))
        {
            _sources[key] = SettingScopes.Profile;
            return;
        }
        _applying = true;
        try
        {
            revert();
        }
        finally
        {
            _applying = false;
        }
    }

    private async Task<bool> TryAsync(Func<Task> write, string failure)
    {
        ErrorMessage = "";
        try
        {
            await write();
            return true;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            logger.LogInformation("{Failure}: {Reason}", failure, ApiFailureText.Describe(ex));
            ErrorMessage = failure;
            return false;
        }
    }
}
