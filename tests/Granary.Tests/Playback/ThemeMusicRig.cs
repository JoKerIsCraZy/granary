using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Playback.Themes;
using Granary.Core.Settings;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using Granary.Tests.Settings;

namespace Granary.Tests.Playback;

/// <summary>
/// The app's theme music on a scripted server: the profile's Title Pages settings, the themes capability and grants, a
/// download that answers a few bytes, and an audio output that only remembers.
/// </summary>
internal sealed class ThemeMusicRig
{
    public ThemeMusicRig(bool themeMusic = true, bool loop = false, IUiDispatcher? dispatcher = null)
    {
        Harness.Answer(TitlePageSettingsTests.EffectivePath, TitlePageSettingsTests.Answer(themeMusic: themeMusic, loop: loop));
        Harness.Answer(ThemeSongsApiTests.CapabilityPath, ThemeSongsApiTests.Available);
        foreach (string key in new[] { TitlePageSettings.ThemeMusicKey, TitlePageSettings.ThemeMusicLoopKey })
        {
            Harness.Silo.On(HttpMethod.Put, "api/v2/settings/values/" + key, ScriptedSilo.NoContent());
        }
        Grants("movie-1", "11");
        Grants("series-1", "21");
        Grants("movie-2", "31");
        Download = new FakeHttpMessageHandler((_, _) => Task.FromResult(DownloadAnswer()));
        Settings = TitlePageSettingsTests.Create(Harness);
        Music = new ThemeMusic(Harness.Session, Settings, new ThemesApi(Harness.Silo.Http), new ThemeAudioDownloader(new FakeHttpClients(Download)),
            Output, dispatcher ?? new ImmediateDispatcher(), Clock, Harness.Logs.For<ThemeMusic>());
    }

    public LibraryHarness Harness { get; } = new();

    public ManualTimeProvider Clock { get; } = new();

    public FakeThemeAudioOutput Output { get; } = new();

    /// <summary>The theme downloads, without credentials.</summary>
    public FakeHttpMessageHandler Download { get; }

    /// <summary>What the next download answers.</summary>
    public HttpStatusCode DownloadStatus { get; set; } = HttpStatusCode.OK;

    public TitlePageSettings Settings { get; }

    public ThemeMusic Music { get; }

    /// <summary>Started: the profile's settings read.</summary>
    public async Task<ThemeMusicRig> StartedAsync()
    {
        await Settings.StartAsync();
        return this;
    }

    public void Grants(string owner, string theme) =>
        Harness.Silo.On(HttpMethod.Post, ThemeSongsApiTests.PlaybackPath(owner, theme),
            ScriptedSilo.Json(HttpStatusCode.OK, ThemeSongsApiTests.Grant($"/api/v2/catalog/items/{owner}/themes/{theme}/audio?token=tkn-{owner}")));

    public int GrantsAsked(string owner, string theme) => Harness.Silo.SentTo(HttpMethod.Post, ThemeSongsApiTests.PlaybackPath(owner, theme)).Count;

    public int CapabilityReads => Harness.Count(ThemeSongsApiTests.CapabilityPath);

    public static ThemeSongSet Set(string owner, params string[] themes) => ThemeMusicPlayerTests.Set(owner, themes);

    /// <summary>The one theme that plays now.</summary>
    public FakeThemeAudio Playing()
    {
        var audio = Assert.Single(Output.Alive);
        Assert.True(audio.IsPlaying);
        return audio;
    }

    private HttpResponseMessage DownloadAnswer() => new(DownloadStatus) { Content = new ByteArrayContent([1, 2, 3]) };
}
