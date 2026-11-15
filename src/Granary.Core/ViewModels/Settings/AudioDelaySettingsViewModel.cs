using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api;
using Granary.Core.Playback;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Playback → Sound: this device's audio sync offset (<see cref="AudioDelay"/>), a slider as the web's device
/// settings show it (components/settings/DeviceSettingGroups.tsx), in the 50 ms steps of Silo's Android app. A change
/// reaches a title that plays at once and is saved for this device once it settles; one made in the player shows here.
/// A read that failed says so with Retry, as the Devices page does; a server that does not know the setting shows no Sound.
/// </summary>
public sealed partial class AudioDelaySettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>A step of Android's audio delay control.</summary>
    public const int StepMs = 50;

    /// <summary>Why the slider is off, in the words of the Devices page's failed reads.</summary>
    public static string LoadFailedText => AppText.Get("granary.settings.playback.audioSyncLoadFailed");

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
    public string ValueText => AppText.Get("web.settings.value.milliseconds", ("count", (int)Milliseconds),
        ("value", ((int)Milliseconds).ToString(CultureInfo.InvariantCulture)));

    /// <summary>This device's value was read: until then the slider is off, so it cannot replace a value it never saw.</summary>
    [ObservableProperty]
    public partial bool IsLoaded { get; private set; }

    /// <summary>The read failed: <see cref="LoadFailedText"/>, with Retry; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadMessage), nameof(ShowsSlider))]
    public partial string LoadMessage { get; private set; } = "";

    public bool HasLoadMessage => LoadMessage.Length > 0;

    public bool ShowsSlider => !HasLoadMessage;

    /// <summary>The server knows the setting; one that refuses it (422) predates it, and Sound does not show.</summary>
    [ObservableProperty]
    public partial bool IsSupported { get; private set; } = true;

    /// <summary>Reads this device's value, as the page opens and on Retry.</summary>
    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        LoadMessage = "";
        try
        {
            await _delay.ReadAsync(cancellationToken);
            Show();
            IsLoaded = true;
        }
        catch (SiloApiException ex) when (ex.Status == 422)
        {
            _logger.LogInformation("The server does not know the audio sync offset");
            IsSupported = false;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken) || ex is InvalidOperationException)
        {
            _logger.LogInformation("The audio sync offset could not be read: {Failure}", ApiFailureText.Describe(ex));
            LoadMessage = LoadFailedText;
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
