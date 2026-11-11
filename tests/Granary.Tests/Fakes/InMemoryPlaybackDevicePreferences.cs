using Granary.Core.Playback;

namespace Granary.Tests.Fakes;

/// <summary>A non-persistent <see cref="IPlaybackDevicePreferences"/> that keeps each value it was asked to save.</summary>
internal sealed class InMemoryPlaybackDevicePreferences : IPlaybackDevicePreferences
{
    public bool DecodesInSoftware { get; set; }

    public List<bool> Saved { get; } = [];

    public PlayerUpDown UpDown { get; set; }

    public List<PlayerUpDown> SavedUpDown { get; } = [];

    public Task SetUpDownAsync(PlayerUpDown upDown)
    {
        UpDown = upDown;
        SavedUpDown.Add(upDown);
        return Task.CompletedTask;
    }

    public bool TimeLeft { get; set; }

    public List<bool> SavedTimeLeft { get; } = [];

    public Task SetTimeLeftAsync(bool shown)
    {
        TimeLeft = shown;
        SavedTimeLeft.Add(shown);
        return Task.CompletedTask;
    }

    public int PassoutThreshold { get; set; } = AutoPlayStreak.DefaultThreshold;

    public List<int> SavedPassoutThreshold { get; } = [];

    public Task SetPassoutThresholdAsync(int episodes)
    {
        PassoutThreshold = episodes;
        SavedPassoutThreshold.Add(episodes);
        return Task.CompletedTask;
    }

    public int ResumeRewindSeconds { get; set; } = ResumeRewind.DefaultSeconds;

    public List<int> SavedResumeRewindSeconds { get; } = [];

    public Task SetResumeRewindSecondsAsync(int seconds)
    {
        ResumeRewindSeconds = seconds;
        SavedResumeRewindSeconds.Add(seconds);
        return Task.CompletedTask;
    }

    public bool DiscordStatus { get; set; }

    public List<bool> SavedDiscordStatus { get; } = [];

    public event EventHandler? DiscordStatusChanged;

    public Task SetDiscordStatusAsync(bool enabled)
    {
        bool changed = enabled != DiscordStatus;
        DiscordStatus = enabled;
        SavedDiscordStatus.Add(enabled);
        if (changed)
        {
            DiscordStatusChanged?.Invoke(this, EventArgs.Empty);
        }
        return Task.CompletedTask;
    }

    public PlayerVolume? Volume { get; set; }

    public List<PlayerVolume> SavedVolume { get; } = [];

    public Task SetVolumeAsync(PlayerVolume volume)
    {
        Volume = volume;
        SavedVolume.Add(volume);
        return Task.CompletedTask;
    }

    public bool PlayerFullScreen { get; set; }

    public List<bool> SavedPlayerFullScreen { get; } = [];

    public Task SetPlayerFullScreenAsync(bool fullScreen)
    {
        PlayerFullScreen = fullScreen;
        SavedPlayerFullScreen.Add(fullScreen);
        return Task.CompletedTask;
    }

    public bool SleepTimerSwitchOff { get; set; }

    /// <summary>How often the Sleep timer switch was dropped from the file.</summary>
    public int SleepTimerSwitchForgotten { get; private set; }

    public Task ForgetSleepTimerSwitchAsync()
    {
        SleepTimerSwitchOff = false;
        SleepTimerSwitchForgotten++;
        return Task.CompletedTask;
    }

    public Task LatestSave => Task.CompletedTask;

    public Task SetDecodesInSoftwareAsync(bool decodesInSoftware)
    {
        DecodesInSoftware = decodesInSoftware;
        Saved.Add(decodesInSoftware);
        return Task.CompletedTask;
    }
}
