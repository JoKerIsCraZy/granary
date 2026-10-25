using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Playback;

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
    // web/src/lib/subtitleAppearance.ts.
    private static readonly (string Hex, string Label)[] FontColors =
    [
        ("#ffffff", "White"), ("#facc15", "Yellow"), ("#22c55e", "Green"), ("#06b6d4", "Cyan"), ("#d946ef", "Magenta"), ("#ef4444", "Red"),
        ("#3b82f6", "Blue"), ("#9ca3af", "Gray"), ("#000000", "Black"),
    ];

    private static readonly (string Hex, string Label)[] BackgroundColors =
        [("#000000", "Black"), ("#374151", "Dark Gray"), ("#1e3a5f", "Navy"), ("#7f1d1d", "Dark Red"), ("#14532d", "Dark Green")];

    private bool _showing;

    public SubtitleAppearanceEditor() => Show(WebDefault);

    /// <summary>Raised when the viewer changes a field, not when <see cref="Show"/> puts a look up.</summary>
    public event EventHandler? Edited;

    /// <summary>The contract's default look (settings manifest): a box, where the apps' built-in fallback has a shadow.</summary>
    public static SubtitleAppearance WebDefault { get; } = SubtitleAppearance.Default with { Background = SubtitleBackground.Box };

    public IReadOnlyList<SettingChoice> FontSizeChoices { get; } =
        [new("small", "Small"), new("medium", "Medium"), new("large", "Large"), new("xlarge", "X-Large"), new("xxlarge", "XX-Large")];

    public IReadOnlyList<SettingChoice> FontFamilyChoices { get; } =
        [new(SubtitleAppearance.SansSerif, "Sans-serif"), new(SubtitleAppearance.Serif, "Serif"), new(SubtitleAppearance.Monospace, "Monospace")];

    public IReadOnlyList<SettingChoice> BackgroundStyleChoices { get; } =
        [new("box", "Box"), new("shadow", "Drop Shadow"), new("outline", "Outline"), new("none", "None")];

    public IReadOnlyList<SettingChoice> PositionChoices { get; } = [new("bottom", "Bottom"), new("lower-third", "Lower Third"), new("top", "Top")];

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

    private static IReadOnlyList<ColorSwatch> Swatches((string Hex, string Label)[] palette, string selected) =>
        [.. palette.Select(c => new ColorSwatch(c.Hex, c.Label, string.Equals(c.Hex, selected, StringComparison.OrdinalIgnoreCase)))];

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
