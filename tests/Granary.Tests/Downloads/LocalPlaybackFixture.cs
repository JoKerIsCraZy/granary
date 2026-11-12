using System.Net;
using Granary.Core.Api.Downloads;
using Granary.Core.Api.Playback;
using Granary.Core.Api.Settings;
using Granary.Core.Downloads;
using Granary.Core.Playback;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Downloads;

/// <summary>
/// Plays downloads of a <see cref="DownloadHarness"/> through a fake engine. By default the server answers the profile's
/// settings (none set) and the episode <see cref="EpisodeId"/> as left at 30 min 30.5 s; <see cref="GoOffline"/> takes it away.
/// </summary>
internal sealed class LocalPlaybackFixture : IAsyncDisposable
{
    public const string EpisodeId = "ep_5";
    public const string SettingsPath = "api/v2/settings/values/effective";
    public const string WatchPath = "api/v2/watch/" + EpisodeId;
    public const string SyncPath = "api/v2/sync/progress";
    public const string ProgressPath = "api/v2/progress";

    public LocalPlaybackFixture()
    {
        Harness.Silo.On(HttpMethod.Get, SettingsPath, ScriptedSilo.Json(HttpStatusCode.OK, """{ "items": [], "revision": 1 }"""));
        Harness.Silo.On(HttpMethod.Get, WatchPath, ScriptedSilo.Json(HttpStatusCode.OK, Watch()));
        Harness.Silo.On(HttpMethod.Put, "api/v2/settings/values/player.playback_speed", ScriptedSilo.NoContent());
        Harness.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "items": [ { "index": 0, "media_item_id": "ep_5", "status": "success" } ], "summary": { "total": 1, "succeeded": 1, "failed": 0 } }"""));
        Harness.Silo.On(HttpMethod.Get, ProgressPath, ScriptedSilo.Json(HttpStatusCode.OK, """{ "items": [], "page": { "has_more": false } }"""));
        Queue = new OfflineProgressQueue(Path.Combine(Harness.Directory, "data", "offline-progress.json"), Harness.Time,
            Harness.Logs.For<OfflineProgressQueue>());
        Changes = new ViewerStateChanges(Stops);
        Sync = new OfflineProgressSync(Queue, new ProgressSyncApi(Harness.Silo.Http), Harness.Store, Harness.Session, Harness.Network, Changes,
            Harness.Time, Harness.Logs.For<OfflineProgressSync>());
        AudioDelay = new AudioDelay(new SettingsApi(Harness.Silo.Http), Harness.Session, new ImmediateDispatcher(), Harness.Time,
            Harness.Logs.For<AudioDelay>());
        Playbacks = new DownloadPlaybacks(Harness.Store, Harness.Session, Create, Harness.Time, Harness.Logs.For<DownloadPlaybacks>());
    }

    public DownloadHarness Harness { get; } = new();

    public FakePlaybackEngine Engine { get; } = new();

    public InMemoryPlaybackDevicePreferences Device { get; } = new();

    public AudioDelay AudioDelay { get; }

    /// <summary>The positions recorded on this PC that the server has not taken yet.</summary>
    public OfflineProgressQueue Queue { get; }

    /// <summary>Sends them, a moment after each record (<see cref="OfflineProgressSync.Settle"/> of the harness's clock).</summary>
    public OfflineProgressSync Sync { get; }

    public DownloadPlaybacks Playbacks { get; }

    /// <summary>The playbacks that run and their stops, as the player counts them.</summary>
    public PlaybackStops Stops { get; } = new();

    /// <summary>What tells the pages that where the viewer left off changed.</summary>
    public ViewerStateChanges Changes { get; }

    public List<ServerPlaybackStatus> Statuses { get; } = [];

    public ScriptedSilo Silo => Harness.Silo;

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The episode as the server has it for the profile: file 4567 with an external English subtitle and, inside the file, a
    /// forced English and a German one; left at <paramref name="position"/> seconds.
    /// </summary>
    public static string Watch(double position = 1830.5, string preferences = "") => $$"""
        {
          "content_id": "ep_5", "type": "episode", "title": "Chapter Five", "series_id": "sr_1", "series_title": "Example Series",
          "season_number": 1, "episode_number": 5,
          "versions": [
            {
              "file_id": "4567", "container": "mkv", "duration_seconds": 7200,
              "audio_tracks": [
                { "language": "en", "codec": "aac", "channels": 6, "default": true },
                { "language": "en", "codec": "aac", "channels": 2, "title": "Commentary" }
              ],
              "subtitle_tracks": [
                { "index": 0, "language": "en", "codec": "subrip", "external": true, "file_name": "Example.en.srt" },
                { "index": 3, "language": "en", "codec": "subrip", "forced": true },
                { "index": 4, "language": "de", "codec": "subrip" }
              ]
            }
          ],
          "user_data": { "position_seconds": {{position.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "duration_seconds": 7200, "is_in_progress": true }
          {{preferences}}
        }
        """;

    /// <summary>The server cannot be reached: every request fails as a refused connection does.</summary>
    public void GoOffline()
    {
        Harness.Silo.On(HttpMethod.Get, SettingsPath, ScriptedSilo.NetworkError());
        Harness.Silo.On(HttpMethod.Get, WatchPath, ScriptedSilo.NetworkError());
        Harness.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.NetworkError());
        Harness.Silo.On(HttpMethod.Get, ProgressPath, ScriptedSilo.NetworkError());
    }

    /// <summary>The file carries a forced English subtitle (stream 1) and a German one (stream 2), as the player lists them.</summary>
    public void FileHasSubtitles()
    {
        Engine.SubtitleTracks.Add(new EngineSubtitleTrack(1, "eng", null, "subrip", IsForced: true, IsExternal: false));
        Engine.SubtitleTracks.Add(new EngineSubtitleTrack(2, "ger", "Deutsch", "subrip", IsForced: false, IsExternal: false));
    }

    public LocalPlayback Create(LocalMedia media)
    {
        var settings = new SettingsApi(Harness.Silo.Http);
        var playback = new LocalPlayback(media, Engine, new ImmediateDispatcher(), Harness.Session,
            new PlaybackPreferencesReader(settings, Harness.Logs.For<PlaybackPreferencesReader>()), new WatchApi(Harness.Silo.Http), settings, Device,
            Harness.Time, Harness.Logs.For<LocalPlayback>(), AudioDelay, Harness.Store, Queue, Sync, Harness.Playing);
        playback.StatusChanged += (_, status) => Statuses.Add(status);
        return playback;
    }

    /// <summary>The download's playback, started; null when the title streams.</summary>
    public async Task<LocalPlayback?> StartAsync(PlayRequest? request = null)
    {
        request ??= PlayRequest.Resume(EpisodeId);
        if (await Playbacks.TryCreateAsync(request, Token) is not LocalPlayback playback)
        {
            return null;
        }
        await playback.StartAsync(request, Token);
        return playback;
    }

    /// <summary>A finished download of the episode, started, with its first frame out.</summary>
    public async Task<LocalPlayback> PlayingAsync(PlayRequest? request = null)
    {
        await Harness.AddPlayableAsync();
        var playback = await StartAsync(request);
        Assert.NotNull(playback);
        Engine.RaiseState(Frame(Engine.Loaded[^1].Start?.TotalSeconds ?? 0));
        return playback;
    }

    public static PlaybackSnapshot Frame(double seconds, bool paused = false) =>
        new(true, paused, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(7200), null, HasStarted: true);

    /// <summary>Lets the flush a record asked for run, and waits for it and for what it wrote.</summary>
    public async Task FlushedAsync()
    {
        Harness.Time.Advance(OfflineProgressSync.Settle);
        await Sync.LatestRun;
        await Queue.LatestSave;
    }

    public async ValueTask DisposeAsync()
    {
        Sync.Dispose();
        await Sync.LatestRun;
        await Queue.LatestSave;
        await Harness.DisposeAsync();
    }
}
