using Granary.Core.ViewModels.Player;

namespace Granary.Tests.ViewModels;

/// <summary>What screen readers and the tooltip say of the player's settings button: the method, then the speed.</summary>
public sealed class SettingsButtonTextTests
{
    [Theory]
    [InlineData("", StreamMethod.None, "Playback settings")]
    [InlineData("1.5×", StreamMethod.None, "Playback settings, 1.5×")]
    [InlineData("", StreamMethod.DirectPlay, "Playback settings, Direct Play")]
    [InlineData("", StreamMethod.DirectStream, "Playback settings, Direct Streaming")]
    [InlineData("1.5×", StreamMethod.Transcode, "Playback settings, Transcode, 1.5×")]
    public void TheName_SaysTheMethod_ThenTheSpeed(string speedBadge, StreamMethod method, string name) =>
        Assert.Equal(name, SettingsButtonText.Name(speedBadge, method));

    [Theory]
    [InlineData(StreamMethod.None, "Playback settings")]
    [InlineData(StreamMethod.DirectPlay, "Playback settings, Direct Play")]
    [InlineData(StreamMethod.Transcode, "Playback settings, Transcode")]
    public void TheTooltip_SaysTheMethod_WithoutTheSpeedThatShowsBeside(StreamMethod method, string tip) =>
        Assert.Equal(tip, SettingsButtonText.Tip(method));
}
