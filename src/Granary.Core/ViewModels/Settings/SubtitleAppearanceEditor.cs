using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Playback;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Settings;

/// <summary>A colour of the appearance palettes: its value, its name, and whether it is the one chosen.</summary>
public sealed record ColorSwatch(string Hex, string Label, bool IsSelected);

/// <summary>
/// The subtitle look being edited, field by field as Silo's web lays them out (lib/subtitleAppearance.ts): size,
/// family, colour, opacity, outline, background and position. The settings page saves it with a button, the player's
/// Appearance panel as it changes; <see cref="Edited"/> tells them apart from <see cref="Show"/>.
/// </summary>
public sealed partial class SubtitleAppearanceEditor : ObservableObject
{
    // web/src/lib/subtitleAppearance.ts, named in the language on screen as they are shown.
    private static readonly (string Hex, Func<string> Label)[] FontColors =
    [
        ("#ffffff", () => AppText.Get("web.settings.subtitleOptions.fontColor.white")),
        ("#facc15", () => AppText.Get("web.settings.subtitleOptions.fontColor.yellow")),
        ("#22c55e", () => AppText.Get("web.settings.subtitleOptions.fontColor.green")),
        ("#06b6d4", () => AppText.Get("web.settings.subtitleOptions.fontColor.cyan")),
        ("#d946ef", () => AppText.Get("web.settings.subtitleOptions.fontColor.magenta")),
        ("#ef4444", () => AppText.Get("web.settings.subtitleOptions.fontColor.red")),
        ("#3b82f6", () => AppText.Get("web.settings.subtitleOptions.fontColor.blue")),
        ("#9ca3af", () => AppText.Get("web.settings.subtitleOptions.fontColor.gray")),
        ("#000000", () => AppText.Get("web.settings.subtitleOptions.fontColor.black")),
    ];

    private static readonly (string Hex, Func<string> Label)[] BackgroundColors =
    [
        ("#000000", () => AppText.Get("web.settings.subtitleOptions.backgroundColor.black")),
        ("#374151", () => AppText.Get("web.settings.subtitleOptions.backgroundColor.darkGray")),
        ("#1e3a5f", () => AppText.Get("web.settings.subtitleOptions.backgroundColor.navy")),
        ("#7f1d1d", () => AppText.Get("web.settings.subtitleOptions.backgroundColor.darkRed")),
        ("#14532d", () => AppText.Get("web.settings.subtitleOptions.backgroundColor.darkGreen")),
    ];

    private bool _showing;

    public SubtitleAppearanceEditor() => Show(WebDefault);

    /// <summary>Raised when the viewer changes a field, not when <see cref="Show"/> puts a look up.</summary>
    public event EventHandler? Edited;

    /// <summary>The contract's default look (settings manifest): a box, where the apps' built-in fallback has a shadow.</summary>
    public static SubtitleAppearance WebDefault { get; } = SubtitleAppearance.Default with { Background = SubtitleBackground.Box };

    public IReadOnlyList<SettingChoice> FontSizeChoices { get; } =
    [
        new("small", AppText.Get("web.settings.subtitleOptions.fontSize.small")),
        new("medium", AppText.Get("web.settings.subtitleOptions.fontSize.medium")),
        new("large", AppText.Get("web.settings.subtitleOptions.fontSize.large")),
        new("xlarge", AppText.Get("web.settings.subtitleOptions.fontSize.xLarge")),
        new("xxlarge", AppText.Get("web.settings.subtitleOptions.fontSize.xxLarge")),
    ];

    public IReadOnlyList<SettingChoice> FontFamilyChoices { get; } =
    [
        new(SubtitleAppearance.SansSerif, AppText.Get("web.settings.subtitleOptions.fontFamily.sansSerif")),
        new(SubtitleAppearance.Serif, AppText.Get("web.settings.subtitleOptions.fontFamily.serif")),
        new(SubtitleAppearance.Monospace, AppText.Get("web.settings.subtitleOptions.fontFamily.monospace")),
    ];

    public IReadOnlyList<SettingChoice> BackgroundStyleChoices { get; } =
    [
        new("box", AppText.Get("web.settings.subtitleOptions.backgroundStyle.box")),
        new("shadow", AppText.Get("web.settings.subtitleOptions.backgroundStyle.dropShadow")),
        new("outline", AppText.Get("web.settings.subtitleOptions.backgroundStyle.outline")),
        new("none", AppText.Get("web.settings.subtitleOptions.backgroundStyle.none")),
    ];

    public IReadOnlyList<SettingChoice> PositionChoices { get; } =
    [
        new("bottom", AppText.Get("web.settings.subtitleOptions.position.bottom")),
        new("lower-third", AppText.Get("web.settings.subtitleOptions.position.lowerThird")),
        new("top", AppText.Get("web.settings.subtitleOptions.position.top")),
    ];

    [ObservableProperty]
    public partial SubtitleAppearance Appearance { get; private set; } = WebDefault;

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

    /// <summary>The background is a box: its opacity and colour apply.</summary>
    [ObservableProperty]
    public partial bool IsBox { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ColorSwatch> BackgroundColorSwatches { get; private set; } = [];

    [ObservableProperty]
    public partial SettingChoice? SelectedPosition { get; set; }

    /// <summary>Puts <paramref name="appearance"/> up as it is, without raising <see cref="Edited"/>.</summary>
    public void Show(SubtitleAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        bool showing = _showing;
        _showing = true;
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
            _showing = showing;
        }
    }

    public void PickFontColor(string hex) => Edit(Appearance with { FontColor = hex });

    public void PickOutlineColor(string hex) => Edit(Appearance with { TextOutlineColor = hex });

    public void PickBackgroundColor(string hex) => Edit(Appearance with { BackgroundColor = hex });

    private static IReadOnlyList<ColorSwatch> Swatches((string Hex, Func<string> Label)[] palette, string selected) =>
        [.. palette.Select(c => new ColorSwatch(c.Hex, c.Label(), string.Equals(c.Hex, selected, StringComparison.OrdinalIgnoreCase)))];

    private void Edit(SubtitleAppearance appearance)
    {
        if (_showing || appearance == Appearance)
        {
            return;
        }
        Show(appearance);
        Edited?.Invoke(this, EventArgs.Empty);
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
}
