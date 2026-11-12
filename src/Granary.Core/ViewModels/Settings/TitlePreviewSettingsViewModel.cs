using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Navigation &amp; Cards → Pages: the switch Title preview at the top and the choice Hero size, both for
/// <see cref="BrowseHeroSetting"/>. They show the acting profile's choices and save a change at once. Home, For You and
/// Discover take it the next time they show. Disposed as the page leaves, so the setting does not keep it.
/// </summary>
public sealed partial class TitlePreviewSettingsViewModel : ObservableObject, IDisposable
{
    private static readonly (BrowseHeroSize Size, string Label)[] SizeLabels = [(BrowseHeroSize.Compact, "Compact"), (BrowseHeroSize.Full, "Full")];

    private readonly BrowseHeroSetting _setting;

    public TitlePreviewSettingsViewModel(BrowseHeroSetting setting)
    {
        _setting = setting;
        Sizes = [.. SizeLabels.Select(s => new SettingsChoice(s.Label, () => _setting.SetSizeAsync(s.Size)))];
        _setting.PropertyChanged += OnSettingChanged;
        ShowSize();
    }

    /// <summary>
    /// Hero size: Compact and Full, for the hero and for Home's banner; the same objects while the choice changes, so focus
    /// stays on the one picked.
    /// </summary>
    public IReadOnlyList<SettingsChoice> Sizes { get; }

    /// <summary>The pages open with the title preview; never fails, and goes back while no profile acts.</summary>
    public bool IsShown
    {
        get => _setting.IsShown;
        set => _ = _setting.SetShownAsync(value);
    }

    public void Dispose() => _setting.PropertyChanged -= OnSettingChanged;

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowseHeroSetting.IsShown))
        {
            OnPropertyChanged(nameof(IsShown));
        }
        else if (e.PropertyName == nameof(BrowseHeroSetting.Size))
        {
            ShowSize();
        }
    }

    private void ShowSize()
    {
        for (int i = 0; i < SizeLabels.Length; i++)
        {
            Sizes[i].IsSelected = SizeLabels[i].Size == _setting.Size;
        }
    }
}
