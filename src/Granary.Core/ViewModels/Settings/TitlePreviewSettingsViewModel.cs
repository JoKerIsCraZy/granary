using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Navigation &amp; Cards → Pages → Title preview at the top: the switch for <see cref="BrowseHeroSetting"/>, which
/// shows the acting profile's choice and saves a change at once. Home, For You and Discover take it the next time they show.
/// Disposed as the page leaves, so the setting does not keep it.
/// </summary>
public sealed partial class TitlePreviewSettingsViewModel : ObservableObject, IDisposable
{
    private readonly BrowseHeroSetting _setting;

    public TitlePreviewSettingsViewModel(BrowseHeroSetting setting)
    {
        _setting = setting;
        _setting.PropertyChanged += OnSettingChanged;
    }

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
    }
}
