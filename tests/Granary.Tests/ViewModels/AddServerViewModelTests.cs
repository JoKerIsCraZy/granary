using System.Net;
using System.Text;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Discovery;
using Granary.Core.Servers;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

public sealed class AddServerViewModelTests
{
    private static readonly SystemInfo V2 = new(2, "digest", "dev", null);
    private const string Ready = """{"needs_setup":false,"wizard_completed":true}""";
    private const string NeedsSetup = """{"needs_setup":true,"wizard_completed":false}""";
    private const string Identity = """{"server_id":"7d1c2a4e-0f3b-4c55-9a7e-2b6f1d8c9e01"}""";
    private const string Branding = """{"server_name":"Living room","login_subtitle":null}""";

    private sealed class ScriptedProbe(Func<Uri, ServerProbeResult> answer) : IServerProbe
    {
        public List<Uri> Probed { get; } = [];

        /// <summary>When set, a probe waits for it (or the cancellation) before it answers.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public async Task<ServerProbeResult> ProbeAsync(Uri baseUri, CancellationToken cancellationToken = default)
        {
            Probed.Add(baseUri);
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }
            return answer(baseUri);
        }
    }

    /// <summary>
    /// The view model over a real <see cref="SystemApi"/> whose HTTP answers are scripted per URL. Unscripted
    /// URLs answer 404 Problem Details.
    /// </summary>
    private sealed class Harness
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _answers = new(StringComparer.Ordinal);

        public Harness(Func<Uri, ServerProbeResult> probe)
        {
            Probe = new ScriptedProbe(probe);
            Http = new FakeHttpMessageHandler((request, _) => Task.FromResult(Respond(request)));
            Discovery = new LanServerDiscovery(Browser, LanProbe, new ManualTimeProvider(), new LogSink().For<LanServerDiscovery>());
            ViewModel = new AddServerViewModel(Probe, new SystemApi(new SiloHttp(new HttpClient(Http))), Registry, Session, Navigation, Discovery,
                new ImmediateDispatcher());
        }

        public ScriptedProbe Probe { get; }

        public LanServerDiscovery Discovery { get; }

        public FakeLanBrowser Browser { get; } = new();

        public FakeLanProbe LanProbe { get; } = new();

        public FakeHttpMessageHandler Http { get; }

        public InMemoryServerRegistry Registry { get; } = new();

        public FakeSessionManager Session { get; } = new();

        public RecordingNavigation Navigation { get; } = new();

        public AddServerViewModel ViewModel { get; }

        public IEnumerable<string> RequestedUrls => Http.Requests.Select(r => r.RequestUri!.AbsoluteUri);

        public Harness Answer(string url, string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _answers[url] = () => Json(status, json, "application/json");
            return this;
        }

        public Harness Unreachable(string url)
        {
            _answers[url] = () => throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused");
            return this;
        }

        /// <summary>A complete, ready server at <paramref name="baseUrl"/>.</summary>
        public Harness ReadyServer(string baseUrl) => this
            .Answer($"{baseUrl}/api/v2/system/setup", Ready)
            .Answer($"{baseUrl}/api/v2/system/identity", Identity)
            .Answer($"{baseUrl}/api/v2/theme/branding", Branding);

        public async Task ConnectAsync(string address)
        {
            ViewModel.Address = address;
            await ViewModel.ConnectCommand.ExecuteAsync(null);
        }

        private HttpResponseMessage Respond(HttpRequestMessage request) =>
            _answers.TryGetValue(request.RequestUri!.AbsoluteUri, out var answer)
                ? answer()
                : Json(HttpStatusCode.NotFound,
                    """{"type":"https://siloserver.org/docs/api/v2/problems/not_found","title":"Not found","status":404}""",
                    "application/problem+json");

        private static HttpResponseMessage Json(HttpStatusCode status, string body, string mediaType) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
    }

    [Fact]
    public async Task Connect_SavesTheServerWithItsIdentityAndName_AndGoesToSignIn()
    {
        var harness = new Harness(uri => new ServerProbeResult.Compatible(uri, V2)).ReadyServer("https://nas.local");

        await harness.ConnectAsync("nas.local");

        var entry = Assert.Single(harness.Registry.Servers);
        Assert.Equal("https://nas.local", entry.BaseUri.OriginalString);
        Assert.Equal("Living room", entry.Name);
        Assert.Equal("7d1c2a4e-0f3b-4c55-9a7e-2b6f1d8c9e01", entry.ServerId);
        Assert.Equal([(AppRoute.SignIn, (object?)entry.Id)], harness.Navigation.Navigations);
        Assert.Equal("https://nas.local", harness.ViewModel.ConnectedServer?.OriginalString);
        Assert.Contains("Living room", harness.ViewModel.Status, StringComparison.Ordinal);
        Assert.All(harness.Http.Requests, r => Assert.Null(r.Headers.Authorization));
    }

    [Fact]
    public async Task Connect_ToASavedServerStillSignedIn_OpensItAgain_InsteadOfSignIn()
    {
        var harness = new Harness(uri => new ServerProbeResult.Compatible(uri, V2)).ReadyServer("https://nas.local");
        var saved = await harness.Registry.AddOrUpdateAsync(new Uri("https://nas.local"), "Living room", null, TestContext.Current.CancellationToken);
        harness.Session.SignedInServers.Add(saved.Id);

        await harness.ConnectAsync("nas.local");

        Assert.Equal(saved.Id, Assert.Single(harness.Registry.Servers).Id);
        Assert.Equal([$"reopen:{saved.Id}:7d1c2a4e-0f3b-4c55-9a7e-2b6f1d8c9e01"], harness.Session.Calls);
        // The shell routes on the session: the page itself goes nowhere.
        Assert.Empty(harness.Navigation.Navigations);
        Assert.Equal("Opening Living room…", harness.ViewModel.Status);
    }

    [Fact]
    public async Task Connect_ToASavedAddress_SettlesItsSession_BeforeTheEntryTakesTheNewIdentity()
    {
        var harness = new Harness(uri => new ServerProbeResult.Compatible(uri, V2)).ReadyServer("https://nas.local");
        var saved = await harness.Registry.AddOrUpdateAsync(new Uri("https://nas.local"), "Living room", "another-server", TestContext.Current.CancellationToken);
        string? idWhenAsked = "";
        harness.Session.ReopenAnswer = id =>
        {
            idWhenAsked = harness.Registry.Find(id)!.ServerId;
            return false;
        };

        await harness.ConnectAsync("nas.local");

        Assert.Equal("another-server", idWhenAsked);
        Assert.Equal("7d1c2a4e-0f3b-4c55-9a7e-2b6f1d8c9e01", harness.Registry.Find(saved.Id)!.ServerId);
        Assert.Equal([(AppRoute.SignIn, (object?)saved.Id)], harness.Navigation.Navigations);
    }

    [Fact]
    public async Task Connect_ToASavedServerRemovedMeanwhile_AddsItAgain()
    {
        var harness = new Harness(uri => new ServerProbeResult.Compatible(uri, V2)).ReadyServer("https://nas.local");
        await harness.Registry.AddOrUpdateAsync(new Uri("https://nas.local"), "Living room", null, TestContext.Current.CancellationToken);
        harness.Session.ReopenAnswer = _ => throw new InvalidOperationException("The server is not saved.");

        await harness.ConnectAsync("nas.local");

        Assert.Equal(AppRoute.SignIn, Assert.Single(harness.Navigation.Navigations).Route);
    }

    [Fact]
    public async Task Connect_ToASavedServerSignedOut_GoesToSignIn()
    {
        var harness = new Harness(uri => new ServerProbeResult.Compatible(uri, V2)).ReadyServer("https://nas.local");
        var saved = await harness.Registry.AddOrUpdateAsync(new Uri("https://nas.local"), "Living room", null, TestContext.Current.CancellationToken);

        await harness.ConnectAsync("nas.local");

        Assert.Equal([(AppRoute.SignIn, (object?)saved.Id)], harness.Navigation.Navigations);
    }

    [Fact]
    public async Task Connect_TriesCandidatesUntilAV2ServerAnswers()
    {
        var harness = new Harness(uri => uri.Scheme == "https"
            ? new ServerProbeResult.NoVerdict(uri, "network: ConnectionError")
            : new ServerProbeResult.Compatible(uri, V2))
            .Unreachable("https://nas.local/api/v2/system/setup")
            .ReadyServer("http://nas.local");

        await harness.ConnectAsync("nas.local");

        Assert.Equal(["https://nas.local", "http://nas.local"], harness.Probe.Probed.Select(u => u.OriginalString));
        Assert.Equal("http://nas.local", Assert.Single(harness.Registry.Servers).BaseUri.OriginalString);
        Assert.Equal(AppRoute.SignIn, Assert.Single(harness.Navigation.Navigations).Route);
    }

    [Fact]
    public async Task Connect_AcceptsACandidateWithoutVerdict_WhenItsSetupCallAnswers()
    {
        var harness = new Harness(uri => new ServerProbeResult.NoVerdict(uri, "HTTP 502")).ReadyServer("https://nas.local");

        await harness.ConnectAsync("nas.local");

        Assert.Single(harness.Probe.Probed);
        var entry = Assert.Single(harness.Registry.Servers);
        Assert.Equal("https://nas.local", entry.BaseUri.OriginalString);
        Assert.Equal([(AppRoute.SignIn, (object?)entry.Id)], harness.Navigation.Navigations);
    }

    [Fact]
    public async Task Connect_StopsWhenTheServerNeedsItsFirstTimeSetup()
    {
        var harness = new Harness(uri => new ServerProbeResult.Compatible(uri, V2))
            .ReadyServer("https://nas.local")
            .Answer("https://nas.local/api/v2/system/setup", NeedsSetup);

        await harness.ConnectAsync("nas.local");

        Assert.Equal(AddServerViewModel.NeedsSetupMessage, harness.ViewModel.Status);
        Assert.Empty(harness.Registry.Servers);
        Assert.Empty(harness.Navigation.Navigations);
        Assert.Equal(["https://nas.local/api/v2/system/setup"], harness.RequestedUrls);
    }

    [Fact]
    public async Task Connect_ToleratesAFailedSetupCall_OnAProvenV2Server()
    {
        var harness = new Harness(uri => new ServerProbeResult.Compatible(uri, V2))
            .ReadyServer("https://nas.local")
            .Answer("https://nas.local/api/v2/system/setup", "", HttpStatusCode.ServiceUnavailable);

        await harness.ConnectAsync("nas.local");

        Assert.Single(harness.Registry.Servers);
        Assert.Single(harness.Navigation.Navigations);
    }

    [Fact]
    public async Task Connect_ToleratesAnIdentityFailure()
    {
        var harness = new Harness(uri => new ServerProbeResult.Compatible(uri, V2))
            .ReadyServer("https://nas.local")
            .Unreachable("https://nas.local/api/v2/system/identity");

        await harness.ConnectAsync("nas.local");

        var entry = Assert.Single(harness.Registry.Servers);
        Assert.Null(entry.ServerId);
        Assert.Equal("Living room", entry.Name);
        Assert.Equal([(AppRoute.SignIn, (object?)entry.Id)], harness.Navigation.Navigations);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"type":"https://siloserver.org/docs/api/v2/problems/not_found","status":404}""")]
    [InlineData(HttpStatusCode.OK, """{"server_name":"   "}""")]
    [InlineData(HttpStatusCode.OK, "<!doctype html><title>proxy</title>")]
    public async Task Connect_NamesTheServerAfterItsHost_WhenBrandingHasNoName(HttpStatusCode status, string body)
    {
        var harness = new Harness(uri => new ServerProbeResult.Compatible(uri, V2))
            .ReadyServer("http://192.168.1.10:8090")
            .Answer("http://192.168.1.10:8090/api/v2/theme/branding", body, status);

        await harness.ConnectAsync("http://192.168.1.10:8090");

        var entry = Assert.Single(harness.Registry.Servers);
        Assert.Equal("192.168.1.10", entry.Name);
        Assert.Equal([(AppRoute.SignIn, (object?)entry.Id)], harness.Navigation.Navigations);
    }

    [Fact]
    public async Task Connect_StopsAtAServerThatMustBeUpdated()
    {
        var harness = new Harness(uri => new ServerProbeResult.UpdateRequired(uri)).ReadyServer("https://old.example.org");

        await harness.ConnectAsync("old.example.org");

        Assert.Single(harness.Probe.Probed);
        Assert.Empty(harness.Http.Requests);
        Assert.Empty(harness.Registry.Servers);
        Assert.Empty(harness.Navigation.Navigations);
        Assert.Null(harness.ViewModel.ConnectedServer);
        Assert.Equal(AddServerViewModel.UpdateRequiredMessage, harness.ViewModel.Status);
    }

    [Fact]
    public async Task Connect_ReportsWhenNoCandidateAnswers()
    {
        var harness = new Harness(uri => new ServerProbeResult.NoVerdict(uri, "timeout"));

        await harness.ConnectAsync("nobody.example");

        Assert.Equal(3, harness.Probe.Probed.Count);
        Assert.Equal(
            ["https://nobody.example/api/v2/system/setup", "http://nobody.example/api/v2/system/setup", "http://nobody.example:8090/api/v2/system/setup"],
            harness.RequestedUrls);
        Assert.Empty(harness.Registry.Servers);
        Assert.Empty(harness.Navigation.Navigations);
        Assert.Null(harness.ViewModel.ConnectedServer);
        Assert.Contains("timeout", harness.ViewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_UpdatesAServerThatIsAlreadySaved()
    {
        var harness = new Harness(uri => new ServerProbeResult.Compatible(uri, V2)).ReadyServer("https://nas.local");
        var saved = await harness.Registry.AddOrUpdateAsync(new Uri("https://nas.local"), "Old name", null, TestContext.Current.CancellationToken);

        await harness.ConnectAsync("https://nas.local/");

        var entry = Assert.Single(harness.Registry.Servers);
        Assert.Equal(saved.Id, entry.Id);
        Assert.Equal("Living room", entry.Name);
        Assert.Equal([(AppRoute.SignIn, (object?)saved.Id)], harness.Navigation.Navigations);
    }

    [Fact]
    public async Task Connect_CanBeCancelled_WithoutSavingOrNavigating()
    {
        var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new HangingProbe(probing);
        var registry = new InMemoryServerRegistry();
        var navigation = new RecordingNavigation();
        var system = new SystemApi(new SiloHttp(new HttpClient(FakeHttpMessageHandler.Returning(HttpStatusCode.OK, Ready))));
        var discovery = new LanServerDiscovery(new NoLanServiceBrowser(), new FakeLanProbe(), TimeProvider.System, new LogSink().For<LanServerDiscovery>());
        var viewModel = new AddServerViewModel(probe, system, registry, new FakeSessionManager(), navigation, discovery, new ImmediateDispatcher())
        {
            Address = "nas.local",
        };

        var connecting = viewModel.ConnectCommand.ExecuteAsync(null);
        await probing.Task.WaitAsync(TestContext.Current.CancellationToken);
        viewModel.ConnectCancelCommand.Execute(null);
        await connecting.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Cancelled.", viewModel.Status);
        Assert.Null(viewModel.ConnectedServer);
        Assert.Empty(registry.Servers);
        Assert.Empty(navigation.Navigations);
    }

    private sealed class HangingProbe(TaskCompletionSource started) : IServerProbe
    {
        public async Task<ServerProbeResult> ProbeAsync(Uri baseUri, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new ServerProbeResult.NoVerdict(baseUri, "unreachable");
        }
    }

    [Fact]
    public async Task Connect_DoesNotProbeAnInvalidAddress()
    {
        var harness = new Harness(uri => new ServerProbeResult.Compatible(uri, V2));

        await harness.ConnectAsync("ftp://nope");

        Assert.Empty(harness.Probe.Probed);
        Assert.Contains("not a server address", harness.ViewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectCommand_IsDisabledWithoutAnAddress()
    {
        var viewModel = new Harness(uri => new ServerProbeResult.UpdateRequired(uri)).ViewModel;

        Assert.False(viewModel.ConnectCommand.CanExecute(null));
        viewModel.Address = "nas";
        Assert.True(viewModel.ConnectCommand.CanExecute(null));
    }

    private const string FoundUrl = "http://192.168.1.6:8090/";
    private const string FoundId = "7d1c2a4e-0f3b-4c55-9a7e-2b6f1d8c9e01";

    private static async Task<Harness> FoundAsync(string advertisedId = FoundId)
    {
        var harness = new Harness(uri => new ServerProbeResult.Compatible(uri, V2)).ReadyServer("http://192.168.1.6:8090");
        harness.LanProbe.Ids[FoundUrl] = advertisedId;
        harness.LanProbe.Names[FoundUrl] = "Living room";
        harness.ViewModel.StartDiscovery();
        harness.Browser.Advertise("a", advertisedId);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!harness.ViewModel.HasFound)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        return harness;
    }

    [Fact]
    public async Task AServerFoundOnThisNetwork_IsListed_ByNameAndAddress()
    {
        var harness = await FoundAsync();

        var row = Assert.Single(harness.ViewModel.Found);
        Assert.Equal("Living room", row.Name);
        Assert.Equal("On this network · 192.168.1.6:8090", row.Detail);
        Assert.Equal("L", row.Initial);
        Assert.False(row.IsConnecting);
        // Every found address is plain HTTP, and the row says so.
        Assert.True(row.IsUnencrypted);
        Assert.Equal("Living room, On this network · 192.168.1.6:8090, not encrypted", row.AccessibleName);
    }

    [Theory]
    [InlineData("https://silo.example/", FoundId)]
    [InlineData("http://192.168.1.6:8090/", null)]
    [InlineData("https://192.168.1.6:8090/", "another-id")]
    public async Task AFoundServerMatchingASavedOne_ByIdOrAddress_IsNotOffered(string saved, string? savedId)
    {
        // The id is public and the name the answerer's own: a look-alike on this network must not become a second entry.
        var harness = new Harness(uri => new ServerProbeResult.Compatible(uri, V2));
        await harness.Registry.AddOrUpdateAsync(new Uri(saved), "Home", savedId, TestContext.Current.CancellationToken);
        harness.LanProbe.Ids[FoundUrl] = FoundId;
        harness.ViewModel.StartDiscovery();

        harness.Browser.Advertise("a", FoundId);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (harness.Discovery.Servers.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.False(harness.ViewModel.HasFound);
    }

    [Fact]
    public async Task AFoundServerSavedSinceItWasListed_IsNotAddedAgain()
    {
        var harness = await FoundAsync();
        var row = harness.ViewModel.Found[0];
        await harness.Registry.AddOrUpdateAsync(new Uri("https://silo.example/"), "Home", FoundId, TestContext.Current.CancellationToken);

        await harness.ViewModel.ConnectToFoundCommand.ExecuteAsync(row);

        Assert.Empty(harness.Probe.Probed);
        Assert.Single(harness.Registry.Servers);
        Assert.False(harness.ViewModel.HasFound);
    }

    [Fact]
    public async Task PickingAFoundServer_GoesTheTypedAddresssWay()
    {
        var harness = await FoundAsync();

        await harness.ViewModel.ConnectToFoundCommand.ExecuteAsync(harness.ViewModel.Found[0]);

        // The same checks as a typed address: info, setup, identity, branding; then saved and on to sign-in.
        Assert.Equal([new Uri(FoundUrl)], harness.Probe.Probed);
        var entry = Assert.Single(harness.Registry.Servers);
        Assert.Equal(FoundUrl, entry.BaseUri.AbsoluteUri);
        Assert.Equal(FoundId, entry.ServerId);
        Assert.Equal([(AppRoute.SignIn, (object?)entry.Id)], harness.Navigation.Navigations);
        Assert.False(harness.ViewModel.IsBusy);
        // Saved now, it is no longer offered as found.
        Assert.False(harness.ViewModel.HasFound);
    }

    [Fact]
    public async Task AFoundServerThatNowAnswersAsAnother_IsNotSaved()
    {
        // Found as srv-x; the address answers the identity in Identity when it is picked.
        var harness = await FoundAsync("srv-x");

        await harness.ViewModel.ConnectToFoundCommand.ExecuteAsync(harness.ViewModel.Found[0]);

        Assert.Empty(harness.Registry.Servers);
        Assert.Empty(harness.Navigation.Navigations);
        Assert.Equal(AddServerViewModel.AnswersAsAnotherMessage, harness.ViewModel.Status);
    }

    [Fact]
    public async Task LeavingTheScreen_StopsBrowsing_AndEmptiesTheList()
    {
        var harness = await FoundAsync();

        harness.ViewModel.StopDiscovery();

        Assert.False(harness.ViewModel.HasFound);
        Assert.False(harness.Browser.IsBrowsing);
    }

    [Fact]
    public void NothingFound_ShowsNothing()
    {
        var harness = new Harness(uri => new ServerProbeResult.Compatible(uri, V2));

        harness.ViewModel.StartDiscovery();

        Assert.True(harness.Browser.IsBrowsing);
        Assert.False(harness.ViewModel.HasFound);
    }

    [Fact]
    public async Task WhileAFoundServerConnects_TheRowSaysSo_TheTypedWayWaits_AndCancelStopsIt()
    {
        var harness = await FoundAsync();
        harness.ViewModel.Address = "nas";
        harness.Probe.Gate = new TaskCompletionSource();

        var connecting = harness.ViewModel.ConnectToFoundCommand.ExecuteAsync(harness.ViewModel.Found[0]);

        Assert.True(harness.ViewModel.Found[0].IsConnecting);
        Assert.True(harness.ViewModel.IsBusy);
        Assert.False(harness.ViewModel.ConnectCommand.CanExecute(null));
        harness.ViewModel.CancelConnectCommand.Execute(null);
        await connecting;
        Assert.Equal("Cancelled.", harness.ViewModel.Status);
        Assert.False(harness.ViewModel.IsBusy);
        Assert.Empty(harness.Registry.Servers);
    }
}
