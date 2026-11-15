using Granary.Core.Playback;
using Granary.Core.Remote;

namespace Granary.Tests.Remote;

/// <summary>
/// What a phone is told of the player's failure: the player's text only when it is one of Granary's own, which name
/// nothing; any other text (a server's or an administrator's words, a message that came from elsewhere) as a fixed one.
/// </summary>
public sealed class RemoteTextsTests
{
    [Theory]
    [InlineData("This title is no longer available.")]
    [InlineData("Could not reach the server. Check the connection and try again.")]
    [InlineData("The server is busy, try again.")]
    [InlineData("The server could not start playback (error 500).")]
    [InlineData("The server is receiving too many requests. Try again in 3 minutes.")]
    [InlineData("The server is receiving too many requests. Try again in a few minutes.")]
    [InlineData("This title cannot be played right now.")]
    [InlineData("The connection to the server was lost.")]
    [InlineData("Playback failed: error loading file")]
    [InlineData("Playback failed: Granary could not open the title")]
    [InlineData("Playback failed: No data within the stall timeout.")]
    [InlineData("This title was started for a profile that is no longer signed in.")]
    [InlineData(ServerPlayback.AdminEndedText)]
    [InlineData(ServerPlayback.AdminStoppedText)]
    public void GranarysOwnFailures_GoAsTheyAre(string message) => Assert.Equal(message, RemoteTexts.PlayerError(message));

    [Theory]
    [InlineData("No such host is known (silo.home:443)")]
    [InlineData("Connection to 192.168.1.66 failed")]
    [InlineData("Stopped by the administrator of silo.home.")]
    [InlineData("Transcoding is off. See https://silo.example/help?ticket=abc123 for more.")]
    [InlineData("Ask your administrator at www.silo.example/admin.")]
    [InlineData("The server could not start playback (error 500). See silo.home")]
    [InlineData("Playback failed: rtsp://192.168.1.66/stream")]
    [InlineData("Playback failed: no such host silo.home")]
    [InlineData("")]
    public void AnyOtherText_GoesAsAFixedOne(string message) =>
        Assert.Equal("This title could not be played on this PC.", RemoteTexts.PlayerError(message));

    [Fact]
    public void NoFailure_IsNone() => Assert.Null(RemoteTexts.PlayerError(null));
}
