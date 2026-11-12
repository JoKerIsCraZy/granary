using System.Collections.Frozen;
using Granary.Core.Api.Admin;
using Granary.Core.Realtime;
using Granary.Core.ViewModels.Admin;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Admin;

/// <summary>
/// The activity indicator, as the web's <c>components/ServerActivity.tsx</c>: the count on the button and its name, when
/// the button shows, the connection's state, and the panel's rows with the web's own words.
/// </summary>
public sealed class ServerActivityViewModelTests : IDisposable
{
    private static readonly FrozenSet<string> Everything = EventsChannels.Admin.ToFrozenSet();

    private readonly ScriptedSilo _silo = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly LogSink _logs = new();
    private readonly ServerActivity _activity;
    private readonly ServerActivityViewModel _indicator;

    public ServerActivityViewModelTests()
    {
        _activity = new ServerActivity(new AdminApi(_silo.Http), _clock, _logs.For<ServerActivity>());
        _indicator = new ServerActivityViewModel(_activity, new ImmediateDispatcher(), _clock);
    }

    public void Dispose()
    {
        _indicator.Dispose();
        _activity.Dispose();
    }

    private static ServerActivityState Live(params AdminPlaybackSession[] sessions) =>
        new() { Channels = Everything, Connection = EventsConnection.Live, Sessions = sessions };

    private static AdminPlaybackSession Session(string id, string method) => new() { SessionId = id, EffectivePlayMethod = method };

    [Fact]
    public void WithoutAnAdministratorsChannel_ThereIsNoButton()
    {
        _indicator.ShowsWhenIdle = true;

        _indicator.Show(new ServerActivityState { Connection = EventsConnection.Live });

        Assert.False(_indicator.IsAvailable);
        Assert.False(_indicator.IsVisible);
    }

    [Fact]
    public void OnARegularPage_TheButtonShowsOnlyWhileSomethingIsGoingOn()
    {
        _indicator.Show(Live());
        Assert.False(_indicator.IsVisible);

        _indicator.Show(Live(Session("s1", AdminPlayMethod.Direct)));
        Assert.True(_indicator.IsVisible);

        _indicator.Show(Live());
        Assert.False(_indicator.IsVisible);
    }

    [Fact]
    public void InTheAdminArea_TheButtonShowsAlso_WhileNothingIsGoingOn()
    {
        _indicator.Show(Live());

        _indicator.ShowsWhenIdle = true;

        Assert.True(_indicator.IsVisible);
        Assert.False(_indicator.HasActivity);
        Assert.True(_indicator.IsEmpty);
        Assert.Equal("Server activity", _indicator.Name);
    }

    [Fact]
    public void AnOpenPanel_KeepsItsButton_WhenTheLastActivityEnds()
    {
        _indicator.Show(Live(Session("s1", AdminPlayMethod.Direct)));
        _indicator.IsPanelOpen = true;

        _indicator.Show(Live());
        Assert.True(_indicator.IsVisible);

        _indicator.IsPanelOpen = false;
        Assert.False(_indicator.IsVisible);
    }

    [Fact]
    public void TheCount_IsOnTheButton_AndInItsName()
    {
        _indicator.Show(Live(Session("s1", AdminPlayMethod.Direct), Session("s2", AdminPlayMethod.Transcode)));

        Assert.Equal(2, _indicator.Total);
        Assert.Equal("2", _indicator.BadgeText);
        Assert.Equal("Server activity: 2 active", _indicator.Name);
        Assert.False(_indicator.IsEmpty);
    }

    [Theory]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    [InlineData(250, "99+")]
    public void Past99_TheCountSays99Plus(int streams, string expected)
    {
        _indicator.Show(Live([.. Enumerable.Range(0, streams).Select(i => Session("s" + i, AdminPlayMethod.Direct))]));

        Assert.Equal(expected, _indicator.BadgeText);
        Assert.Equal(expected, _indicator.StreamCountText);
        Assert.Equal($"Server activity: {streams} active", _indicator.Name);
    }

    [Fact]
    public void Streams_AreCountedPerPlayMethod_InTheWebsOrder()
    {
        _indicator.Show(Live(Session("s1", AdminPlayMethod.Transcode), Session("s2", AdminPlayMethod.Direct), Session("s3", "something_new"),
            Session("s4", AdminPlayMethod.DirectStream), Session("s5", AdminPlayMethod.Direct), Session("s6", AdminPlayMethod.Remux),
            new AdminPlaybackSession { SessionId = "s7" }));

        Assert.True(_indicator.ShowsStreams);
        Assert.True(_indicator.HasStreams);
        Assert.Equal(7, _indicator.StreamCount);
        Assert.Equal(["2 Direct Play", "1 Remux", "1 Direct Stream", "1 Transcode", "1 Unknown", "1 Unknown"], _indicator.Streams.Select(row => row.Text));
        Assert.Equal(AdminPlayMethod.Direct, _indicator.Streams[0].Method);
    }

    [Fact]
    public void ARowThatStays_IsTheSameRow_WithItsNewCount()
    {
        _indicator.Show(Live(Session("s1", AdminPlayMethod.Transcode), Session("s2", AdminPlayMethod.Direct)));
        var direct = _indicator.Streams[0];
        var transcode = _indicator.Streams[1];

        _indicator.Show(Live(Session("s1", AdminPlayMethod.Transcode), Session("s3", AdminPlayMethod.Transcode), Session("s4", AdminPlayMethod.Remux)));

        Assert.Equal(["1 Remux", "2 Transcode"], _indicator.Streams.Select(row => row.Text));
        Assert.Same(transcode, _indicator.Streams[1]);
        Assert.DoesNotContain(direct, _indicator.Streams);
    }

    [Fact]
    public void AServerWithoutTheSessions_HasNoStreamsSection()
    {
        _indicator.Show(new ServerActivityState { Channels = Everything, Connection = EventsConnection.Live });

        Assert.False(_indicator.ShowsStreams);
        Assert.Empty(_indicator.Streams);
    }

    [Fact]
    public void TheConnection_IsNamedInThePanel_AndOnTheButtonOnlyOnceItStayedAway()
    {
        _indicator.Show(Live(Session("s1", AdminPlayMethod.Direct)));
        Assert.Equal("", _indicator.ConnectionText);

        _indicator.Show(Live(Session("s1", AdminPlayMethod.Direct)) with { Connection = EventsConnection.Disconnected });
        Assert.Equal("Disconnected", _indicator.ConnectionText);
        Assert.True(_indicator.HasConnectionText);
        _indicator.Show(Live(Session("s1", AdminPlayMethod.Direct)) with { Connection = EventsConnection.Connecting });
        Assert.Equal("Connecting…", _indicator.ConnectionText);
        _clock.Advance(ServerActivityViewModel.ConnectionProblemDelay - TimeSpan.FromMilliseconds(1));
        Assert.False(_indicator.ShowsConnectionProblem);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(_indicator.ShowsConnectionProblem);

        _indicator.Show(Live(Session("s1", AdminPlayMethod.Direct)));
        Assert.False(_indicator.ShowsConnectionProblem);
        Assert.False(_indicator.HasConnectionText);
    }

    [Fact]
    public void AConnectionBackInTime_NeverShowsTheProblem()
    {
        _indicator.Show(Live() with { Connection = EventsConnection.Disconnected });
        _clock.Advance(TimeSpan.FromSeconds(1));
        _indicator.Show(Live() with { Connection = EventsConnection.Connecting });
        _clock.Advance(TimeSpan.FromSeconds(1));

        _indicator.Show(Live());
        _clock.Advance(ServerActivityViewModel.ConnectionProblemDelay);

        Assert.False(_indicator.ShowsConnectionProblem);
    }

    [Fact]
    public void ViewAll_AsksTheShellForTheActivityPage()
    {
        int asked = 0;
        _indicator.StreamsRequested += (_, _) => asked++;

        _indicator.ViewStreamsCommand.Execute(null);

        Assert.Equal(1, asked);
    }

    [Fact]
    public void TheActivitysNews_IsShown()
    {
        // What the stream brings reaches the indicator: a connection the server gave the scans, which need no read.
        long feed = _activity.Follow(ScriptedSilo.Base);
        _activity.Connection(feed, EventsConnection.Live);
        _activity.Handle(feed, EventsFrames.Parse("""{ "type": "subscribed", "request_id": "req-1", "channels": ["catalog", "scans"] }""")!);
        Assert.True(_indicator.IsAvailable);

        _activity.Follow(null);
        Assert.False(_indicator.IsAvailable);
    }
}
