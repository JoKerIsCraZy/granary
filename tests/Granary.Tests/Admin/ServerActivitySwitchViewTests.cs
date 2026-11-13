using System.Collections.Frozen;
using Granary.Core.Api.Admin;
using Granary.Core.Auth;
using Granary.Core.Realtime;
using Granary.Core.ViewModels.Admin;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using Granary.Tests.Realtime;

namespace Granary.Tests.Admin;

/// <summary>
/// The activity indicator under its switch in Settings (<see cref="ActivityIndicatorSetting"/>): off, the button shows
/// nowhere, and it is back as soon as the switch is on again.
/// </summary>
public sealed class ServerActivitySwitchViewTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly ScriptedSilo _silo = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly LogSink _logs = new();
    private readonly FakeSessionManager _session = new();
    private readonly ServerActivity _activity;
    private readonly ActivityIndicatorSetting _setting;
    private readonly ServerActivityViewModel _indicator;

    public ServerActivitySwitchViewTests()
    {
        _session.State = SessionState.Ready;
        _session.ActiveServer = ScriptedSilo.Server;
        _session.ProfileId = "7";
        _session.Account = ServerActivityHarness.Administrator;
        _activity = new ServerActivity(new AdminApi(_silo.Http), _clock, _logs.For<ServerActivity>());
        _setting = new ActivityIndicatorSetting(Path.Combine(_directory, "activity-indicator.json"), _session, new ImmediateDispatcher(),
            _logs.For<ActivityIndicatorSetting>());
        _indicator = new ServerActivityViewModel(_activity, new ImmediateDispatcher(), _clock, _setting);
    }

    public void Dispose()
    {
        _indicator.Dispose();
        _activity.Dispose();
        _setting.Saving.Wait(TestContext.Current.CancellationToken);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // A stream plays, on a connection the server gave every administrator's channel.
    private static ServerActivityState Busy() => new()
    {
        Channels = EventsChannels.Admin.ToFrozenSet(),
        Connection = EventsConnection.Live,
        Sessions = [new AdminPlaybackSession { SessionId = "s1", EffectivePlayMethod = AdminPlayMethod.Direct }],
        Scans = [],
    };

    [Fact]
    public void AtFirst_TheSwitchIsOn_AndTheButtonShows()
    {
        _indicator.Show(Busy());

        Assert.True(_indicator.IsVisible);
    }

    [Fact]
    public async Task SwitchedOff_TheButtonShowsNowhere_NotInTheAdminAreaEither()
    {
        _indicator.Show(Busy());
        _indicator.ShowsWhenIdle = true;
        var changed = new List<string?>();
        _indicator.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await _setting.SetShownAsync(false);

        Assert.False(_indicator.IsVisible);
        Assert.Contains(nameof(ServerActivityViewModel.IsVisible), changed);
    }

    [Fact]
    public async Task SwitchedOff_AnOpenPanelNoLongerKeepsItsButton()
    {
        _indicator.Show(Busy());
        _indicator.IsPanelOpen = true;

        await _setting.SetShownAsync(false);

        // The shell closes the panel of a button that goes.
        Assert.False(_indicator.IsVisible);
    }

    [Fact]
    public async Task SwitchedOff_NewsOfTheServer_DoesNotBringTheButtonBack()
    {
        await _setting.SetShownAsync(false);

        _indicator.Show(Busy());
        _indicator.ShowsWhenIdle = true;
        _indicator.IsPanelOpen = true;

        Assert.False(_indicator.IsVisible);
    }

    [Fact]
    public async Task SwitchedOnAgain_TheButtonIsBack_WithoutARestart()
    {
        _indicator.Show(Busy());
        await _setting.SetShownAsync(false);
        var changed = new List<string?>();
        _indicator.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await _setting.SetShownAsync(true);

        Assert.True(_indicator.IsVisible);
        Assert.Contains(nameof(ServerActivityViewModel.IsVisible), changed);
    }

    [Fact]
    public async Task AProfileThatLeftItsSwitchOn_HasItsButton()
    {
        _indicator.Show(Busy());
        await _setting.SetShownAsync(false);

        _session.ProfileId = "8";
        _session.RaiseChanged();

        Assert.True(_indicator.IsVisible);
    }

    [Fact]
    public async Task Disposed_TheIndicatorLetsGoOfTheSwitch()
    {
        _indicator.Show(Busy());
        var changed = new List<string?>();
        _indicator.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _indicator.Dispose();
        await _setting.SetShownAsync(false);

        Assert.Empty(changed);
    }
}
