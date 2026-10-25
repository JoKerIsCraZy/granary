using Granary.Core.Api.Playback;

namespace Granary.Core.Playback;

/// <summary>What this machine's player can decode, for the capability report; the platform layer probes it once.</summary>
public interface IPlaybackDeviceProbe
{
    PlaybackDeviceFacts Facts { get; }
}
