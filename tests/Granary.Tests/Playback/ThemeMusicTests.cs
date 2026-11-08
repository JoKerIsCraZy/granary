using System.Net;
using Granary.Core.Auth;
using Granary.Core.Playback.Themes;
using Granary.Tests.Auth;
using Granary.Tests.Profiles;
using static Granary.Tests.Playback.ThemeMusicRig;

namespace Granary.Tests.Playback;

/// <summary>When title pages play their theme music, as the apps decide it (Android's ThemeMusicCoordinator, Apple's ThemeMusicCenter).</summary>
public sealed class ThemeMusicTests
{
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(300);

    private readonly object _page = new();
    private readonly object _nextPage = new();

    [Fact]
    public async Task WithThemeMusicOff_NothingPlays_NorIsTheServerAsked()
    {
        var rig = await new ThemeMusicRig(themeMusic: false).StartedAsync();

        rig.Music.Show(_page, Set("movie-1", "11"));

        Assert.Empty(rig.Output.Made);
        Assert.Equal(0, rig.CapabilityReads);
        Assert.Equal(0, rig.GrantsAsked("movie-1", "11"));
    }

    [Fact]
    public async Task WithItOn_ThePagesFirstThemePlays_OnceTheServerSaysItServesThem()
    {
        var rig = await new ThemeMusicRig().StartedAsync();

        rig.Music.Show(_page, Set("movie-1", "11", "12"));

        await AuthPipeline.Eventually(() => rig.Output.Made.Count == 1);
        Assert.Equal(1, rig.CapabilityReads);
        Assert.Equal(1, rig.GrantsAsked("movie-1", "11"));
        var audio = rig.Playing();
        audio.Start();
        rig.Clock.Advance(Fade);
        Assert.Equal(ThemeMusicPlayer.Volume, audio.Volume, 6);
        Assert.True(rig.Music.IsAudible);
    }

    [Theory]
    [InlineData("""{ "state": "available", "allowed": false, "revision": "r" }""")]
    [InlineData("""{ "state": "disabled", "allowed": true, "revision": "r" }""")]
    [InlineData("""{ "state": "unsupported", "revision": "r" }""")]
    public async Task AServerThatDoesNotServeThemesToTheProfile_PlaysNothing(string capability)
    {
        var rig = new ThemeMusicRig();
        rig.Harness.Answer(ThemeSongsApiTests.CapabilityPath, capability);
        await rig.StartedAsync();

        rig.Music.Show(_page, Set("movie-1", "11"));

        await AuthPipeline.Eventually(() => rig.CapabilityReads == 1);
        Assert.Empty(rig.Output.Made);
        Assert.Equal(0, rig.GrantsAsked("movie-1", "11"));
    }

    [Fact]
    public async Task AServerThatPredatesThemes_PlaysNothing()
    {
        var rig = new ThemeMusicRig();
        rig.Harness.Answer(ThemeSongsApiTests.CapabilityPath, ScriptedSilo.Problem(404, "not_found"));
        await rig.StartedAsync();

        rig.Music.Show(_page, Set("movie-1", "11"));

        await AuthPipeline.Eventually(() => rig.CapabilityReads == 1);
        Assert.Empty(rig.Output.Made);
    }

    [Theory]
    [InlineData("routed", true)]
    [InlineData("local_direct_play", false)]
    public async Task AProxyNodesAddress_IsFetched_OnlyWhileTheServerRoutesThemes(string delivery, bool fetched)
    {
        var rig = new ThemeMusicRig();
        rig.Harness.Answer(ThemeSongsApiTests.CapabilityPath, $$"""{ "state": "available", "allowed": true, "delivery": "{{delivery}}", "revision": "r" }""");
        rig.Harness.Silo.On(HttpMethod.Post, ThemeSongsApiTests.PlaybackPath("movie-1", "11"),
            ScriptedSilo.Json(HttpStatusCode.OK, ThemeSongsApiTests.Grant("https://proxy.example/theme-audio/abc?token=t")));
        await rig.StartedAsync();

        rig.Music.Show(_page, Set("movie-1", "11"));

        await AuthPipeline.Eventually(() => rig.GrantsAsked("movie-1", "11") >= 1);
        if (fetched)
        {
            await AuthPipeline.Eventually(() => rig.Output.Made.Count == 1);
            Assert.Equal("https://proxy.example/theme-audio/abc?token=t", Assert.Single(rig.Download.Requests).RequestUri!.AbsoluteUri);
        }
        else
        {
            Assert.Empty(rig.Download.Requests);
            Assert.Empty(rig.Output.Made);
        }
    }

    [Fact]
    public async Task TheServersAnswer_StandsAMinute()
    {
        var rig = await new ThemeMusicRig().StartedAsync();
        rig.Music.Show(_page, Set("movie-1", "11"));
        await AuthPipeline.Eventually(() => rig.Output.Made.Count == 1);

        rig.Music.Show(_nextPage, Set("movie-2", "31"));
        rig.Clock.Advance(TimeSpan.FromSeconds(59));
        rig.Music.Show(_page, Set("movie-1", "11"));
        Assert.Equal(1, rig.CapabilityReads);
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        rig.Music.Show(_nextPage, Set("movie-2", "31"));

        await AuthPipeline.Eventually(() => rig.CapabilityReads == 2);
    }

    [Fact]
    public async Task WhileThePageLoads_NoThemePlays()
    {
        var rig = await new ThemeMusicRig().StartedAsync();

        rig.Music.Show(_page, null);

        Assert.Empty(rig.Output.Made);
        Assert.Equal(0, rig.GrantsAsked("movie-1", "11"));
    }

    [Fact]
    public async Task ATitleWithoutThemes_PlaysNothing()
    {
        var rig = await new ThemeMusicRig().StartedAsync();

        rig.Music.Show(_page, Set("movie-1"));

        await AuthPipeline.Eventually(() => rig.CapabilityReads == 1);
        Assert.Empty(rig.Output.Made);
    }

    [Fact]
    public async Task LeavingThePage_FadesTheThemeOut_AndLetsItGoTwoSecondsLater()
    {
        var rig = await PlayingAsync();
        var audio = rig.Playing();

        rig.Music.Hide(_page);
        rig.Clock.Advance(Fade);
        Assert.False(audio.IsPlaying);
        Assert.Equal(0, audio.Volume, 6);
        Assert.False(audio.IsDisposed);
        rig.Clock.Advance(ThemeMusic.ReleaseGrace);

        Assert.True(audio.IsDisposed);
        Assert.False(rig.Music.IsAudible);
    }

    [Fact]
    public async Task APageThatFollowsWithTheSameTheme_GoesOnWhereItWas()
    {
        var rig = await PlayingAsync("series-1", "21");
        var audio = rig.Playing();

        rig.Music.Hide(_page);
        rig.Music.Show(_nextPage, null);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Music.Show(_nextPage, Set("series-1", "21"));
        rig.Clock.Advance(ThemeMusic.ReleaseGrace);

        Assert.Same(audio, rig.Playing());
        Assert.Equal(ThemeMusicPlayer.Volume, audio.Volume, 6);
        Assert.Equal(1, rig.GrantsAsked("series-1", "21"));
    }

    [Fact]
    public async Task AfterTheGrace_TheSameThemeStartsOver()
    {
        var rig = await PlayingAsync();
        var first = rig.Playing();

        rig.Music.Hide(_page);
        rig.Clock.Advance(ThemeMusic.ReleaseGrace);
        rig.Music.Show(_nextPage, Set("movie-1", "11"));

        await AuthPipeline.Eventually(() => rig.Output.Made.Count == 2);
        Assert.True(first.IsDisposed);
        Assert.Equal(2, rig.GrantsAsked("movie-1", "11"));
    }

    [Fact]
    public async Task AnotherPagesTheme_TakesOver()
    {
        var rig = await PlayingAsync();
        var first = rig.Playing();

        rig.Music.Show(_nextPage, Set("movie-2", "31"));
        await AuthPipeline.Eventually(() => rig.Output.Made.Count == 2);
        rig.Clock.Advance(Fade);

        Assert.True(first.IsDisposed);
        Assert.Equal(1, rig.GrantsAsked("movie-2", "31"));
    }

    [Fact]
    public async Task APageLeavingAfterAnotherShowed_ChangesNothing()
    {
        var rig = await PlayingAsync();
        rig.Music.Show(_nextPage, Set("movie-1", "11"));

        rig.Music.Hide(_page);
        rig.Clock.Advance(ThemeMusic.ReleaseGrace + Fade);

        rig.Playing();
    }

    [Fact]
    public async Task ThePlayer_StopsTheThemeAtOnce_AndItStartsOverAfter()
    {
        var rig = await PlayingAsync();
        var first = rig.Playing();

        rig.Music.OtherMediaPlaying = true;
        Assert.True(first.IsDisposed);
        Assert.False(rig.Music.IsAudible);
        rig.Music.Show(_nextPage, Set("movie-2", "31"));
        Assert.Single(rig.Output.Made);

        rig.Music.OtherMediaPlaying = false;

        await AuthPipeline.Eventually(() => rig.Output.Made.Count == 2);
        Assert.Equal(1, rig.GrantsAsked("movie-2", "31"));
    }

    [Fact]
    public async Task Minimized_TheThemeFadesOut_AndStopsAfterTheGrace()
    {
        var rig = await PlayingAsync();
        var audio = rig.Playing();

        rig.Music.IsMinimized = true;
        rig.Clock.Advance(Fade);
        Assert.False(audio.IsPlaying);
        rig.Clock.Advance(ThemeMusic.ReleaseGrace);
        Assert.True(audio.IsDisposed);

        rig.Music.IsMinimized = false;
        await AuthPipeline.Eventually(() => rig.Output.Made.Count == 2);
    }

    [Fact]
    public async Task RestoredSoon_TheThemeGoesOnWhereItWas()
    {
        var rig = await PlayingAsync();
        var audio = rig.Playing();

        rig.Music.IsMinimized = true;
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Music.IsMinimized = false;
        rig.Clock.Advance(ThemeMusic.ReleaseGrace);

        Assert.Same(audio, rig.Playing());
    }

    [Fact]
    public async Task AnotherProfile_StopsTheThemeAtOnce_AndIsAskedAgain()
    {
        var rig = await PlayingAsync();
        var audio = rig.Playing();

        rig.Harness.Session.ProfileId = "8";
        rig.Harness.Session.RaiseChanged();

        Assert.True(audio.IsDisposed);
        await rig.Settings.Working;
        rig.Music.Show(_nextPage, Set("movie-1", "11"));
        await AuthPipeline.Eventually(() => rig.Output.Made.Count == 2);
        Assert.Equal(2, rig.CapabilityReads);
    }

    [Fact]
    public async Task SignedOut_TheThemeStopsAtOnce()
    {
        var rig = await PlayingAsync();
        var audio = rig.Playing();

        rig.Harness.Session.State = SessionState.SignedOut;
        rig.Harness.Session.ProfileId = null;
        rig.Harness.Session.RaiseChanged();

        Assert.True(audio.IsDisposed);
    }

    [Fact]
    public async Task AGrantReadWhileTheProfileChanged_IsDropped()
    {
        var rig = new ThemeMusicRig();
        var release = new TaskCompletionSource();
        rig.Harness.Silo.On(HttpMethod.Post, ThemeSongsApiTests.PlaybackPath("movie-1", "11"),
            Browse.LibraryHarness.Held(release, ThemeSongsApiTests.Grant()));
        await rig.StartedAsync();
        rig.Music.Show(_page, Set("movie-1", "11"));
        await AuthPipeline.Eventually(() => rig.GrantsAsked("movie-1", "11") == 1);

        rig.Harness.Session.ProfileId = "8";
        release.SetResult();

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(rig.Output.Made);
        Assert.Empty(rig.Download.Requests);
    }

    [Fact]
    public async Task TurningThemeMusicOff_FadesTheThemeOut()
    {
        var rig = await PlayingAsync();
        var audio = rig.Playing();

        await rig.Settings.SetPlaysThemeMusicAsync(false);
        rig.Clock.Advance(Fade);

        Assert.True(audio.IsDisposed);
    }

    [Fact]
    public async Task WithLoop_TheThemeRepeats()
    {
        var rig = await new ThemeMusicRig(loop: true).StartedAsync();

        rig.Music.Show(_page, Set("movie-1", "11"));

        await AuthPipeline.Eventually(() => rig.Output.Made.Count == 1);
        Assert.True(rig.Playing().IsLooping);
    }

    [Fact]
    public async Task AFailedDownload_IsTriedOnce_AndNeverLogsItsAddress()
    {
        var rig = await new ThemeMusicRig().StartedAsync();
        rig.DownloadStatus = HttpStatusCode.Forbidden;

        rig.Music.Show(_page, Set("movie-1", "11"));

        await AuthPipeline.Eventually(() => rig.Download.Requests.Count == 1);
        Assert.Empty(rig.Output.Made);
        Assert.DoesNotContain("tkn-", rig.Harness.Logs.All, StringComparison.Ordinal);
        Assert.Contains("theme music could not be played", rig.Harness.Logs.All, StringComparison.Ordinal);
    }

    private async Task<ThemeMusicRig> PlayingAsync(string owner = "movie-1", string theme = "11")
    {
        var rig = await new ThemeMusicRig().StartedAsync();
        rig.Music.Show(_page, Set(owner, theme));
        await AuthPipeline.Eventually(() => rig.Output.Made.Count == 1);
        rig.Playing().Start();
        rig.Clock.Advance(Fade);
        return rig;
    }
}
