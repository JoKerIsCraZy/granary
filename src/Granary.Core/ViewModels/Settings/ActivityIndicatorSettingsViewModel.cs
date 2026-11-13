using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Realtime;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Navigation &amp; Cards → Server activity: the switch Activity indicator, for
/// <see cref="ActivityIndicatorSetting"/>. The group shows only for an administrator's account
/// (<see cref="IsOffered"/>); the switch shows the acting profile's choice and saves a change at once, which the
/// indicator follows at once. Disposed as the page leaves, so the setting does not keep it.
/// </summary>
public sealed partial class ActivityIndicatorSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ActivityIndicatorSetting _setting;

    public ActivityIndicatorSettingsViewModel(ActivityIndicatorSetting setting)
    {
        _setting = setting;
        _setting.PropertyChanged += OnSettingChanged;
    }

    /// <summary>An administrator's account is signed in: everyone else has no indicator, and no group for it.</summary>
    public bool IsOffered => _setting.IsOffered;

    /// <summary>The acting profile has its indicator; never fails, and goes back while no profile acts.</summary>
    public bool IsShown
    {
        get => _setting.IsShown;
        set => _ = _setting.SetShownAsync(value);
    }

    public void Dispose() => _setting.PropertyChanged -= OnSettingChanged;

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActivityIndicatorSetting.IsShown))
        {
            OnPropertyChanged(nameof(IsShown));
        }
        else if (e.PropertyName == nameof(ActivityIndicatorSetting.IsOffered))
        {
            OnPropertyChanged(nameof(IsOffered));
        }
    }
}
