using Granary.Core.Playback;

namespace Granary.Tests.Fakes;

/// <summary>A non-persistent <see cref="IPlaybackDevicePreferences"/> that keeps each value it was asked to save.</summary>
internal sealed class InMemoryPlaybackDevicePreferences : IPlaybackDevicePreferences
{
    public bool DecodesInSoftware { get; set; }

    public List<bool> Saved { get; } = [];

    public Task LatestSave => Task.CompletedTask;

    public Task SetDecodesInSoftwareAsync(bool decodesInSoftware)
    {
        DecodesInSoftware = decodesInSoftware;
        Saved.Add(decodesInSoftware);
        return Task.CompletedTask;
    }
}
