using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Playback;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Playback → Sound: this device's audio sync offset (<see cref="AudioDelay"/>), a slider as the web's device
/// settings show it (components/settings/DeviceSettingGroups.tsx), in the 50 ms steps of Silo's Android app. A change
/// reaches a title that plays at once and is saved for this device once it settles; one made in the player shows here.
/// </summary>
public sealed partial class AudioDelaySettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>A step of Android's audio delay control.</summary>
    public const int StepMs = 50;

    private readonly AudioDelay _delay;
    private readonly ILogger<AudioDelaySettingsViewModel> _logger;

    // The value shown is the delay's, not the viewer's change.
    private bool _showing;

    public AudioDelaySettingsViewModel(AudioDelay delay, ILogger<AudioDelaySettingsViewModel> logger)
    {
        _delay = delay;
        _logger = logger;
        _delay.Changed += OnDelayChanged;
    }

    public static double Minimum => -AudioDelay.MaxMs;

    public static double Maximum => AudioDelay.MaxMs;

    /// <summary>The slider's value, in milliseconds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    public partial double Milliseconds { get; set; }

    /// <summary>"-250 milliseconds", as the web's slider reads out the value and the setting's unit.</summary>
    public string ValueText => string.Create(CultureInfo.InvariantCulture, $"{(int)Milliseconds} milliseconds");

    /// <summary>This device's value was read: until then the slider is off, so it cannot replace a value it never saw.</summary>
    [ObservableProperty]
    public partial bool IsLoaded { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _delay.ReadAsync(cancellationToken);
            Show();
            IsLoaded = true;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken) || ex is InvalidOperationException)
        {
            _logger.LogInformation("The audio sync offset could not be read: {Failure}", ApiFailureText.Describe(ex));
        }
    }

    /// <summary>The page left: it stops following the delay.</summary>
    public void Dispose() => _delay.Changed -= OnDelayChanged;

    partial void OnMillisecondsChanged(double value)
    {
        if (_showing || !IsLoaded)
        {
            return;
        }
        double step = Math.Round(value / StepMs, MidpointRounding.AwayFromZero) * StepMs;
        if (step != value)
        {
            Milliseconds = step;
            return;
        }
        _delay.Set((int)step);
    }

    private void OnDelayChanged(object? sender, EventArgs e) => Show();

    private void Show()
    {
        _showing = true;
        try
        {
            Milliseconds = _delay.Milliseconds;
        }
        finally
        {
            _showing = false;
        }
    }
}
