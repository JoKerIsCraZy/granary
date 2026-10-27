using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Overlays;

namespace Granary.Core.ViewModels.Settings;

/// <summary>A badge style to pick, with the web's words for it; <see cref="Pick"/> takes <see cref="Id"/>.</summary>
public sealed record BadgeStyleChoice(OverlayPresetId Id, string Label, string Description, bool IsSelected, ICommand Pick);

/// <summary>A palette colour for a badge; <see cref="Pick"/> takes <see cref="Hex"/>.</summary>
public sealed record BadgeColorChoice(string Hex, string Label, bool IsSelected, ICommand Pick);

/// <summary>
/// Settings → Navigation &amp; Cards → Badges, as Silo's web has it (pages/settings/CardOverlaySettings.tsx): badges on or
/// off, their style, and per badge whether it shows, its corner, colour and icon, folded by category. Every change saves
/// to the profile at once (<see cref="CardOverlaySettings"/>), so the web shows the same badges; Reset returns the profile
/// to the server's.
/// </summary>
public sealed partial class BadgeSettingsViewModel : ObservableObject, IDisposable
{
    public const string ResetTitle = "Reset badges?";
    public const string ResetText = "Your badge choices are cleared, and this profile follows your server's badge settings from then on.";

    private static readonly (OverlayCategory Category, string Title, string Description)[] Categories =
    [
        (OverlayCategory.Tech, "Media Info", "Technical details from your media files."),
        (OverlayCategory.Ratings, "Ratings & Certifications", "Scores from external sources and content ratings."),
        (OverlayCategory.Metadata, "Content Metadata", "Information about the content itself."),
        (OverlayCategory.Ribbons, "Status & Awards", "Series lifecycle, request status and award badges. Some require upcoming data sources."),
    ];

    private readonly CardOverlaySettings _settings;
    private readonly Dictionary<string, BadgeRowViewModel> _rows = new(StringComparer.Ordinal);
    private string _offeredKey = "";

    public BadgeSettingsViewModel(CardOverlaySettings settings)
    {
        _settings = settings;
        _settings.PropertyChanged += OnSettingsChanged;
        Show();
    }

    /// <summary>Badges on posters at all; the switch saves at once.</summary>
    public bool IsEnabled
    {
        get => _settings.IsEnabled;
        set => _ = _settings.SetEnabledAsync(value);
    }

    public bool CanEdit => _settings.CanEdit;

    /// <summary>The profile chose its own badges; Reset shows only then, as on the web.</summary>
    public bool HasOverride => _settings.HasOverride;

    [ObservableProperty]
    public partial IReadOnlyList<BadgeStyleChoice> Styles { get; private set; } = [];

    /// <summary>The categories with at least one badge the server offers.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<BadgeGroupViewModel> Groups { get; private set; } = [];

    /// <summary>Reads the badges again, as the page opens: another device may have changed them.</summary>
    public Task LoadAsync() => _settings.RefreshAsync();

    [RelayCommand]
    private Task PickStyleAsync(OverlayPresetId preset) =>
        preset == _settings.Prefs.Preset ? Task.CompletedTask : _settings.SetPrefsAsync(_settings.Prefs with { Preset = preset });

    /// <summary>Clears the profile's badge choices; the page asks first.</summary>
    [RelayCommand]
    private Task ResetAsync() => _settings.ResetAsync();

    /// <summary>Saves one badge's change on top of the badges as they are now.</summary>
    internal Task UpdateAsync(string id, Func<OverlayItemConfig, OverlayItemConfig> change)
    {
        var prefs = _settings.Prefs;
        var next = change(prefs.ItemOf(id));
        return next == prefs.ItemOf(id) ? Task.CompletedTask : _settings.SetPrefsAsync(prefs.With(id, next));
    }

    public void Dispose() => _settings.PropertyChanged -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CardOverlaySettings.IsEnabled):
                OnPropertyChanged(nameof(IsEnabled));
                break;
            case nameof(CardOverlaySettings.CanEdit):
                OnPropertyChanged(nameof(CanEdit));
                break;
            case nameof(CardOverlaySettings.HasOverride):
                OnPropertyChanged(nameof(HasOverride));
                break;
            case nameof(CardOverlaySettings.Prefs) or nameof(CardOverlaySettings.ShownRatingSources):
                Show();
                break;
        }
    }

    // The rows stay the same objects while the badges change, so a row's open Adjust panel follows its badge.
    private void Show()
    {
        var prefs = _settings.Prefs;
        Styles = [.. OverlayPreset.All.Select(preset =>
            new BadgeStyleChoice(preset.Id, preset.Label, preset.Description, preset.Id == prefs.Preset, PickStyleCommand))];

        var offered = OverlayRegistry.All.Where(_settings.IsOffered).ToList();
        string offeredKey = string.Join(",", offered.Select(def => def.Id));
        foreach (var def in offered)
        {
            if (!_rows.TryGetValue(def.Id, out var row))
            {
                _rows[def.Id] = row = new BadgeRowViewModel(this, def);
            }
            row.Show(prefs);
        }
        if (offeredKey != _offeredKey)
        {
            _offeredKey = offeredKey;
            Groups = [.. Categories
                .Select(category => new BadgeGroupViewModel(category.Title, category.Description,
                    [.. offered.Where(def => def.Category == category.Category).Select(def => _rows[def.Id])]))
                .Where(group => group.Rows.Count > 0)];
        }
        foreach (var group in Groups)
        {
            group.Count();
        }
    }
}

/// <summary>One category of badges, folded; its header says how many are on.</summary>
public sealed partial class BadgeGroupViewModel(string title, string description, IReadOnlyList<BadgeRowViewModel> rows) : ObservableObject
{
    public string Title { get; } = title;

    public string Description { get; } = description;

    public IReadOnlyList<BadgeRowViewModel> Rows { get; } = rows;

    /// <summary>"None on", or "2 of 5 on".</summary>
    [ObservableProperty]
    public partial string OnText { get; private set; } = "";

    /// <summary>The category is open; all start folded, as on the web.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    internal void Count()
    {
        int on = Rows.Count(row => row.IsEnabled);
        OnText = on == 0 ? "None on" : $"{on} of {Rows.Count} on";
    }
}

/// <summary>A badge's switch, and behind Adjust its corner, colour and icon.</summary>
public sealed partial class BadgeRowViewModel : ObservableObject
{
    public const string SuppressedHint = "Hidden while the combined Resolution + HDR badge is on.";

    /// <summary>The web's badge colours (lib/overlays/presets.ts, ACCENT_PALETTE).</summary>
    public static readonly IReadOnlyList<(string Hex, string Label)> Palette =
    [
        ("#f5c518", "Gold"), ("#fa320a", "Tomato"), ("#f97316", "Orange"), ("#f59e0b", "Amber"), ("#10b981", "Emerald"),
        ("#06b6d4", "Cyan"), ("#3b82f6", "Blue"), ("#6366f1", "Indigo"), ("#8b5cf6", "Violet"), ("#ec4899", "Pink"),
        ("#64748b", "Slate"), ("#ffffff", "White"),
    ];

    private readonly BadgeSettingsViewModel _owner;
    private readonly OverlayDef _def;
    // Set while the row shows the saved badges, so showing them saves nothing.
    private bool _showing;

    internal BadgeRowViewModel(BadgeSettingsViewModel owner, OverlayDef def)
    {
        _owner = owner;
        _def = def;
    }

    public string Id => _def.Id;

    public string Label => _def.Label;

    public string Description => _def.Description;

    public bool CanShowIcon => _def.IconCapable;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial bool ShowIcon { get; set; }

    /// <summary>Why the badge may not show: the combined badge hides it, or its data is not there yet.</summary>
    [ObservableProperty]
    public partial string? Hint { get; private set; }

    /// <summary>The badge's corner; the four flags light the corner picker's buttons.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTopLeft), nameof(IsTopRight), nameof(IsBottomLeft), nameof(IsBottomRight))]
    public partial OverlayPosition Position { get; private set; }

    public bool IsTopLeft => Position == OverlayPosition.TopLeft;

    public bool IsTopRight => Position == OverlayPosition.TopRight;

    public bool IsBottomLeft => Position == OverlayPosition.BottomLeft;

    public bool IsBottomRight => Position == OverlayPosition.BottomRight;

    [ObservableProperty]
    public partial IReadOnlyList<BadgeColorChoice> Swatches { get; private set; } = [];

    /// <summary>The badge has no colour of its own choosing: the style's, or the badge's own default.</summary>
    [ObservableProperty]
    public partial bool IsDefaultColor { get; private set; }

    /// <summary>Moves the badge to a corner, named as <see cref="OverlayPosition"/> ("TopLeft"); the picker's buttons pass it as text.</summary>
    [RelayCommand]
    private Task PickCornerAsync(string corner) =>
        Enum.TryParse<OverlayPosition>(corner, out var position) && Enum.IsDefined(position)
            ? _owner.UpdateAsync(Id, item => item with { Position = position })
            : Task.CompletedTask;

    /// <summary>Picks a palette colour; null goes back to the default.</summary>
    [RelayCommand]
    private Task PickColorAsync(string? hex) => _owner.UpdateAsync(Id, item => item with { AccentColor = hex });

    internal void Show(CardOverlayPrefs prefs)
    {
        var item = prefs.ItemOf(Id);
        _showing = true;
        try
        {
            IsEnabled = item.Enabled;
            ShowIcon = _def.IconCapable && (item.ShowIcon ?? OverlayPreset.Of(prefs.Preset).PreferIcon);
        }
        finally
        {
            _showing = false;
        }
        Hint = item.Enabled && prefs.IsSuppressed(Id) ? SuppressedHint : _def.AvailabilityNote;
        Position = item.Position;
        string? color = item.AccentColor?.ToLowerInvariant();
        Swatches = [.. Palette.Select(swatch => new BadgeColorChoice(swatch.Hex, swatch.Label, swatch.Hex == color, PickColorCommand))];
        IsDefaultColor = color is null;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_showing)
        {
            _ = _owner.UpdateAsync(Id, item => item with { Enabled = value });
        }
    }

    partial void OnShowIconChanged(bool value)
    {
        if (!_showing)
        {
            _ = _owner.UpdateAsync(Id, item => item with { ShowIcon = value });
        }
    }
}
