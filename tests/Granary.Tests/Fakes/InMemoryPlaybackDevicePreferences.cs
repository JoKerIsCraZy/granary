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
    public int PassoutThreshold { get; set; } = AutoPlayStreak.DefaultThreshold;

    public List<int> SavedPassoutThreshold { get; } = [];

    public Task SetPassoutThresholdAsync(int episodes)
    {
        PassoutThreshold = episodes;
        SavedPassoutThreshold.Add(episodes);
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
