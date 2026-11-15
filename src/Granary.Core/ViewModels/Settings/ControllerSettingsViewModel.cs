using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Controller;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Navigation &amp; Cards → Game controller: the switch for <see cref="ControllerSetting"/>, off by default. It
/// saves a change at once, and the window follows it at once. Disposed as the page leaves, so the setting does not keep it.
/// </summary>
public sealed partial class ControllerSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ControllerSetting _setting;

    public ControllerSettingsViewModel(ControllerSetting setting)
    {
        _setting = setting;
        _setting.PropertyChanged += OnSettingChanged;
    }

    /// <summary>Granary takes a game controller's input; never fails.</summary>
    public bool IsEnabled
    {
        get => _setting.IsEnabled;
        set => _ = _setting.SetEnabledAsync(value);
    }

    public void Dispose() => _setting.PropertyChanged -= OnSettingChanged;

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ControllerSetting.IsEnabled))
        {
            OnPropertyChanged(nameof(IsEnabled));
        }
    }
}
