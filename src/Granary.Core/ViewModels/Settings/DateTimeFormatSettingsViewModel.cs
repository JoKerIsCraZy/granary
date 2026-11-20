using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Settings;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Accessibility → Date &amp; time, as the web's group of that name (<c>pages/settings/AccessibilitySettings.tsx</c>):
/// the profile's date format and time format as choice buttons, each saved at once (<see cref="DateTimeFormatSetting"/>),
/// and a preview of today in them. Auto is the settings manifest's "Match device", where the web says browser.
/// </summary>
public sealed partial class DateTimeFormatSettingsViewModel : ObservableObject, IDisposable
{
    private readonly DateTimeFormatSetting _setting;
    private readonly TimeProvider _time;

    public DateTimeFormatSettingsViewModel(DateTimeFormatSetting setting, TimeProvider time)
    {
        _setting = setting;
        _time = time;
        DateChoices = [.. DateTimePreferences.DateFormats.Select(format =>
            new SettingsChoice(() => DateLabel(format), () => Working = _setting.ChooseDateFormatAsync(format)))];
        TimeChoices = [.. DateTimePreferences.TimeFormats.Select(format =>
            new SettingsChoice(() => TimeLabel(format), () => Working = _setting.ChooseTimeFormatAsync(format)))];
        Select();
        _setting.PropertyChanged += OnSettingChanged;
    }

    /// <summary>Match device, then DD/MM/YYYY, MM/DD/YYYY and YYYY-MM-DD, each its own label, as on the web.</summary>
    public IReadOnlyList<SettingsChoice> DateChoices { get; }

    /// <summary>Match device, 12-hour and 24-hour.</summary>
    public IReadOnlyList<SettingsChoice> TimeChoices { get; }

    /// <summary>The choices can be used: the profile's formats are known.</summary>
    public bool CanChoose => _setting.IsReady;

    /// <summary>The profile's formats could not be read; Retry reads them again.</summary>
    public bool LoadFailed => _setting.LoadFailed;

    /// <summary>"Preview: 11/19/2026 · Nov 19, 2026 · 3:04 PM": today in the formats in use, as the web's line under the choices.</summary>
    public string Preview
    {
        get
        {
            var now = _time.GetLocalNow().DateTime;
            using var _ = DateText.Use(_setting.Preferences);
            return AppText.Get("web.settings.accessibility.dateTimePreview",
                ("short", DateText.Date(now)), ("medium", DateText.MediumDate(now)), ("time", DateText.Time(now)));
        }
    }

    /// <summary>The choice in flight, for tests.</summary>
    internal Task Working { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the profile's formats again as the page opens: another device may have changed them.</summary>
    public Task LoadAsync() => Working = _setting.RefreshAsync();

    public void Dispose() => _setting.PropertyChanged -= OnSettingChanged;

    [RelayCommand]
    private Task RetryAsync() => LoadAsync();

    private static string DateLabel(string format) => format == DateTimePreferences.Auto
        ? AppText.Get("settingsManifest.ui.date_format.option.auto")
        : format;

    private static string TimeLabel(string format) => format switch
    {
        DateTimePreferences.TwelveHours => AppText.Get("web.settings.accessibility.time12h"),
        DateTimePreferences.TwentyFourHours => AppText.Get("web.settings.accessibility.time24h"),
        _ => AppText.Get("settingsManifest.ui.time_format.option.auto"),
    };

    private void Select()
    {
        var preferences = _setting.Preferences;
        for (int i = 0; i < DateChoices.Count; i++)
        {
            DateChoices[i].IsSelected = DateTimePreferences.DateFormats[i] == preferences.DateFormat;
        }
        for (int i = 0; i < TimeChoices.Count; i++)
        {
            TimeChoices[i].IsSelected = DateTimePreferences.TimeFormats[i] == preferences.TimeFormat;
        }
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DateTimeFormatSetting.Preferences):
                Select();
                OnPropertyChanged(nameof(Preview));
                break;
            case nameof(DateTimeFormatSetting.IsReady):
                OnPropertyChanged(nameof(CanChoose));
                break;
            case nameof(DateTimeFormatSetting.LoadFailed):
                OnPropertyChanged(nameof(LoadFailed));
                break;
        }
    }
}
