using Granary.Core.Playback;
using Granary.Core.Remote;
using Granary.Core.Texts;

namespace Granary.Tests.Remote;

/// <summary>
/// What a phone is told of the player's failure: the player's text only when it is one of Granary's own, which name
/// nothing; any other text (a server's or an administrator's words, a message that came from elsewhere) as a fixed one.
/// </summary>
public sealed class RemoteTextsTests
{
    [Theory]
    [InlineData("This title is no longer available.")]
    [InlineData("Can't reach the server. Check your connection and try again.")]
    [InlineData("The server is busy, try again.")]
    [InlineData("The server could not start playback (error 500).")]
    [InlineData("The server is receiving too many requests. Try again in 3 minutes.")]
    [InlineData("The server is receiving too many requests. Try again in 1 minute.")]
    [InlineData("Too many requests. Please wait a moment and try again.")]
    [InlineData("Playback is unavailable right now.")]
    [InlineData("The connection to the server was lost.")]
    [InlineData("Playback error: loading failed")]
    [InlineData("Playback error: unrecognized file format")]
    [InlineData("Playback error: video output initialization failed")]
    [InlineData("Playback error: unknown error")]
    [InlineData("Playback error: Granary could not open the title")]
    [InlineData("Playback error: No data within the stall timeout.")]
    [InlineData("An administrator ended this playback session.")]
    [InlineData("An administrator stopped this playback session.")]
    [InlineData("Sign in to play from this server.")]
    [InlineData("Your sign-in session has expired. Sign in again to continue playback.")]
    [InlineData("No playable version found")]
    [InlineData("This title was started for a profile that is no longer signed in.")]
    public void GranarysOwnFailures_GoAsTheyAre(string message) => Assert.Equal(message, RemoteTexts.PlayerError(message));

    [Fact]
    public void AnAdministratorsEndOrStop_GoesAsGranaryWordsIt()
    {
        Assert.Equal(ServerPlayback.AdminEndedText, RemoteTexts.PlayerError(ServerPlayback.AdminEndedText));
        Assert.Equal(ServerPlayback.AdminStoppedText, RemoteTexts.PlayerError(ServerPlayback.AdminStoppedText));
    }

    [Fact]
    public void GranarysOwnFailures_InTheLanguageOnScreen_GoInEnglish()
    {
        using var german = AppText.Use(TextLanguage.Load("de"));
        var shown = new[]
        {
            AppText.Get("granary.player.error.connectionLost"),
            AppText.Get("granary.player.error.startFailed", ("status", 503)),
            AppText.Get("granary.apiFailure.tooManyRequests", ("count", 3)),
            AppText.Get("web.player.error.playbackError", ("message", "loading failed")),
            ServerPlayback.AdminEndedText,
        };
        Assert.DoesNotContain("The connection to the server was lost.", shown);

        Assert.Equal(
            [
                "The connection to the server was lost.",
                "The server could not start playback (error 503).",
                "The server is receiving too many requests. Try again in 3 minutes.",
                "Playback error: loading failed",
                "An administrator ended this playback session.",
            ],
            shown.Select(RemoteTexts.PlayerError));
    }

    [Fact]
    public void ThePlayersStartFailures_InTheLanguageOnScreen_GoInEnglish()
    {
        using var german = AppText.Use(TextLanguage.Load("de"));
        var shown = new[]
        {
            MediaAuthorizationException.SignedOutText,
            MediaAuthorizationException.SessionEndedText,
            MediaAuthorizationException.RenewFailedText,
            PlaybackUnavailableException.NoServerText,
            PlaybackUnavailableException.NoProfileText,
            PlaybackUnavailableException.NoFileText,
            PlaybackUnavailableException.RoomFileUnavailableText,
            PlaybackUnavailableException.NotOfferedText,
            PlaybackUnavailableException.StartedForAnotherProfile,
        };
        Assert.DoesNotContain("Sign in to play from this server.", shown);
        Assert.DoesNotContain("No playable version found", shown);

        Assert.Equal(
            [
                "Sign in to play from this server.",
                "Your sign-in session has expired. Sign in again to continue playback.",
                "The server could not renew the sign-in. Try again in a moment.",
                "Choose a server to play from.",
                "Choose a profile to play from this server.",
                "No playable version found",
                "The room's file is not available here.",
                "This server does not offer playback to this app.",
                "This title was started for a profile that is no longer signed in.",
            ],
            shown.Select(RemoteTexts.PlayerError));
    }

    [Fact]
    public void ANumberedFailure_InArabicDigits_GoesInEnglish()
    {
        using var arabic = AppText.Use(TextLanguage.Load("ar"));
        string shown = AppText.Get("granary.player.error.startFailed", ("status", 503));
        Assert.DoesNotContain("503", shown, StringComparison.Ordinal);

        Assert.Equal("The server could not start playback (error 503).", RemoteTexts.PlayerError(shown));
    }

    [Fact]
    public void AServersWords_InTheLanguageOnScreen_GoAsAFixedOne()
    {
        using var german = AppText.Use(TextLanguage.Load("de"));

        Assert.Equal("This title could not be played on this PC.", RemoteTexts.PlayerError("Gestoppt vom Administrator von silo.home."));
        Assert.Equal("This title could not be played on this PC.",
            RemoteTexts.PlayerError(AppText.Get("web.player.error.playbackError", ("message", "no such host silo.home"))));
    }

    [Theory]
    [InlineData("No such host is known (silo.home:443)")]
    [InlineData("Connection to 192.168.1.66 failed")]
    [InlineData("Stopped by the administrator of silo.home.")]
    [InlineData("Transcoding is off. See https://silo.example/help?ticket=abc123 for more.")]
    [InlineData("Ask your administrator at www.silo.example/admin.")]
    [InlineData("The server could not start playback (error 500). See silo.home")]
    [InlineData("Playback error: rtsp://192.168.1.66/stream")]
    [InlineData("Playback error: no such host silo.home")]
    [InlineData("Playback error: no such host mediaserver")]
    [InlineData("Playback error: error loading file")]
    [InlineData("Playback error: ")]
    [InlineData("The server could not start playback (error 1000000000).")]
    [InlineData("The server could not start playback (error ⁵⁰³).")]
    [InlineData("The server could not start playback (error ⁨503⁩).")]
    [InlineData("")]
    public void AnyOtherText_GoesAsAFixedOne(string message) =>
        Assert.Equal("This title could not be played on this PC.", RemoteTexts.PlayerError(message));

    [Fact]
    public void ALongTextFullOfNumbers_GoesAsAFixedOne()
    {
        string flood = string.Join("a", Enumerable.Range(0, 20_000));

        Assert.Equal("This title could not be played on this PC.", RemoteTexts.PlayerError(flood));
        Assert.Equal("This title could not be played on this PC.", RemoteTexts.PlayerError("Error 1, 2, 3, 4, 5 and 503."));
    }

    [Fact]
    public void ANetworkPathTheLocalPlayerRefuses_GoesAsItIs() =>
        Assert.Equal("Network paths cannot be played; copy the file to this computer.",
            RemoteTexts.PlayerError("Network paths cannot be played; copy the file to this computer."));

    [Fact]
    public void NoFailure_IsNone() => Assert.Null(RemoteTexts.PlayerError(null));
}
