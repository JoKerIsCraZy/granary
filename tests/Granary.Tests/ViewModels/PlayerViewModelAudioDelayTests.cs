using System.Text.Json;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.ViewModels;

/// <summary>AUDIO DELAY in the player's settings menu, as the Sync page of Silo's Android player has it.</summary>
public sealed class PlayerViewModelAudioDelayTests
{
    private const string ValuePath = "api/v2/settings/values/" + AudioDelay.Key;

    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly ScriptedSilo _silo = new();
    private readonly AudioDelay _delay;

    public PlayerViewModelAudioDelayTests()
    {
        _delay = new AudioDelay(new SettingsApi(_silo.Http),
            new FakeSessionManager { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" },
            new ImmediateDispatcher(), _time, new LogSink().For<AudioDelay>());
        _silo.On(HttpMethod.Put, ValuePath, ScriptedSilo.NoContent());
    }

    private IReadOnlyList<SentRequest> Saves => _silo.SentTo(HttpMethod.Put, ValuePath);

    private PlayerViewModel Create() => new(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () => new FakeServerPlayback(),
        new PlaybackStops(), _time, audioDelay: _delay);

    private async Task<PlayerViewModel> PlayingAsync()
    {
        var viewModel = Create();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        return viewModel;
    }

    [Fact]
    public async Task AServerTitle_ShowsThisDevicesDelay_AndAStepIsSavedForThisDevice()
    {
        _delay.Take(100);
        using var viewModel = await PlayingAsync();
        Assert.Equal("+100 ms", viewModel.AudioDelayText);

        viewModel.ChangeAudioDelay(PlayerViewModel.AudioDelayStepMs);
        viewModel.ChangeAudioDelay(PlayerViewModel.AudioDelayStepMs);

        Assert.Equal(200, viewModel.AudioDelayMs);
        Assert.Equal(200, _delay.Milliseconds);
        _time.Advance(TimeSpan.FromSeconds(1));
        await _delay.LatestSave;
        var save = Assert.Single(Saves);
        Assert.Equal(200, JsonDocument.Parse(save.Body!).RootElement.GetProperty("value").GetInt32());
        Assert.Equal("?scope=profile_device", save.Uri.Query);
    }

    [Fact]
    public async Task ADelayChangedInSettings_ShowsInTheMenu_UntilThePlayerGoes()
    {
        var viewModel = await PlayingAsync();

        _delay.Set(-200);
        Assert.Equal("\u2212200 ms", viewModel.AudioDelayText);

        viewModel.Dispose();
        _delay.Set(300);
        Assert.Equal(-200, viewModel.AudioDelayMs);
    }

    [Fact]
    public async Task TheDelay_StopsAtFiveSecondsEitherWay_AndResetGoesBackToNone()
    {
        _delay.Take(4980);
        using var viewModel = await PlayingAsync();
        Assert.True(viewModel.CanDelayAudioLater);

        viewModel.ChangeAudioDelay(PlayerViewModel.AudioDelayStepMs);
        Assert.Equal(5000, viewModel.AudioDelayMs);
        Assert.False(viewModel.CanDelayAudioLater);
        Assert.True(viewModel.CanDelayAudioEarlier);

        viewModel.ResetAudioDelay();
        Assert.Equal("0 ms", viewModel.AudioDelayText);
        Assert.False(viewModel.CanResetAudioDelay);
        Assert.Equal(0, _delay.Milliseconds);
    }

    [Fact]
    public async Task ALocalFile_PlaysWithThisDevicesDelay_AndAStepIsOnlyThePlayers()
    {
        _delay.Take(100);
        using var viewModel = Create();

        await viewModel.StartAsync(PlayRequest.Direct(@"C:\Videos\clip.mkv"));
        Assert.Equal(0.1, _engine.AudioDelay);
        Assert.Equal(100, viewModel.AudioDelayMs);

        viewModel.ChangeAudioDelay(-PlayerViewModel.AudioDelayStepMs);
        Assert.Equal(0.05, _engine.AudioDelay, 6);
        Assert.Equal(50, viewModel.AudioDelayMs);
        Assert.Equal(100, _delay.Milliseconds);
        _time.Advance(TimeSpan.FromSeconds(2));
        await _delay.LatestSave;
        Assert.Empty(Saves);
        Assert.True(viewModel.ShowsAudioDelay);
    }

    [Fact]
    public async Task AServerThatDoesNotKnowTheSetting_GetsNoRow()
    {
        var playback = new FakeServerPlayback { Preferences = PlaybackPreferences.Default with { AudioDelayRefused = true } };
        using var viewModel = new PlayerViewModel(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () => playback,
            new PlaybackStops(), _time, audioDelay: _delay);

        _delay.Take(300);

        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));

        Assert.False(viewModel.ShowsAudioDelay);
        // Another server's delay is not this title's, not even in the hidden row.
        Assert.Equal(0, viewModel.AudioDelayMs);
        _delay.Set(-100);
        Assert.Equal(0, viewModel.AudioDelayMs);
    }
}
