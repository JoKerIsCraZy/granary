using System.Net;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Playback;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Player;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.ViewModels;

/// <summary>The player's Edit markers: offered to an account that may, with the draft on the seek bar.</summary>
public sealed class PlayerViewModelMarkerEditTests
{
    private readonly FakePlaybackEngine _engine = new();
    private readonly List<FakeServerPlayback> _playbacks = [];
    private readonly ScriptedSilo _silo = new();
    private readonly FakeSessionManager _session = new() { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "1" };

    private FakeServerPlayback Playback => _playbacks[^1];

    private async Task<PlayerViewModel> PlayingAsync(string role = "admin")
    {
        _session.Account = new Account("1", "alex", "", role, [], true, false, null);
        _silo.On(HttpMethod.Get, "api/v2/profiles", ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "items": [ { "id": "1", "name": "Alex", "is_primary": true, "has_pin": false, "is_child": false } ], "avatar_upload_enabled": false }"""));
        var viewModel = new PlayerViewModel(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () =>
        {
            var playback = new FakeServerPlayback { PlayingFileId = "42", DurationSeconds = 1500 };
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), permissions: new AccountPermissions(_session, new ProfilesApi(_silo.Http), new LogSink().For<AccountPermissions>()));
        await viewModel.StartAsync(PlayRequest.Resume("episode-1"));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        _engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1500), null, HasStarted: true));
        if (role == "admin")
        {
            await Until(() => viewModel.CanEditMarkers);
        }
        return viewModel;
    }

    [Fact]
    public async Task AnAdminOnThePrimaryProfile_GetsTheButton()
    {
        var viewModel = await PlayingAsync();

        Assert.True(viewModel.CanEditMarkers);
    }

    [Fact]
    public async Task AViewer_DoesNot()
    {
        var viewModel = await PlayingAsync("user");

        Assert.False(viewModel.CanEditMarkers);
        viewModel.ToggleMarkerEditCommand.Execute(null);
        Assert.False(viewModel.MarkerEditor.IsEditing);
    }

    [Fact]
    public async Task WhileEditing_TheSeekBarShowsTheDraft()
    {
        var viewModel = await PlayingAsync();
        Playback.RaiseMarkers([new PlaybackMarker(WatchMarkerKinds.Intro, 3, 12)]);

        viewModel.ToggleMarkerEditCommand.Execute(null);
        viewModel.SetMarkerEdgeHere(WatchMarkerKinds.Intro, MarkerEdge.End);

        Assert.True(viewModel.MarkerEditor.IsEditing);
        Assert.Equal([new PlaybackMarker(WatchMarkerKinds.Intro, 3, 30)], viewModel.SeekBarMarkers);

        viewModel.ToggleMarkerEditCommand.Execute(null);
        Assert.Equal([new PlaybackMarker(WatchMarkerKinds.Intro, 3, 12)], viewModel.SeekBarMarkers);
    }

    [Fact]
    public async Task Save_GoesToThePlayback()
    {
        var viewModel = await PlayingAsync();
        viewModel.ToggleMarkerEditCommand.Execute(null);
        viewModel.SetMarkerEdgeHere(WatchMarkerKinds.Credits, MarkerEdge.Start);

        await viewModel.MarkerEditor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new WatchMarker(30, 90), Assert.Single(Playback.MarkerSaves)[WatchMarkerKinds.Credits]);
        Assert.False(viewModel.MarkerEditor.IsEditing);
    }

    [Fact]
    public async Task ANewTitle_ClosesTheEditor()
    {
        var viewModel = await PlayingAsync();
        viewModel.ToggleMarkerEditCommand.Execute(null);

        await viewModel.StartAsync(PlayRequest.Resume("episode-2"));

        Assert.False(viewModel.MarkerEditor.IsEditing);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(5);
        }
    }
}
