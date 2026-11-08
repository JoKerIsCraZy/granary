using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.Theme;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Core.Theme;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.ViewModels;

/// <summary>The server's accent, as the web paints it over Cinema Dark (lib/accentColor.ts).</summary>
public sealed class ServerAccentTests
{
    private const string BrandingPath = "api/v2/theme/branding";

    private const string Blue = """{ "server_name": "Elena's Silo", "accent_color": "#1d4ed8" }""";

    private const string NoAccent = """{ "server_name": "Elena's Silo" }""";

    private static readonly string WordmarkUrl = ScriptedSilo.Base.AbsoluteUri.TrimEnd('/') + "/api/v2/branding/assets/wordmark?v=1.webp";

    private readonly LibraryHarness _harness = new();
    private readonly FakeLocalNetwork _network = new();
    private readonly InMemoryServerRegistry _registry = new();

    public ServerAccentTests() => _registry.Add(ScriptedSilo.Server);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private ServerAccent Create() =>
        new(_harness.Session, _registry, new BrandingApi(_harness.Silo.Http), _network, new ImmediateDispatcher(), _harness.Clock,
            _harness.Logs.For<ServerAccent>());

    // The server as saved with what it last answered, active.
    private void Remember(SavedBranding branding)
    {
        var server = ScriptedSilo.Server with { Branding = branding };
        _registry.Add(server);
        _harness.Session.ActiveServer = server;
    }

    private SavedBranding? Remembered(string serverId = "srv1") => _registry.Find(serverId)?.Branding;

    private int Reads(Uri? server = null) =>
        _harness.Silo.Sent.Count(r => r.Method == HttpMethod.Get && r.Uri.GetLeftPart(UriPartial.Path) == ScriptedSilo.Url(BrandingPath, server).AbsoluteUri);

    // The retry that falls due next, and its read.
    private async Task<TimeSpan> RetryAsync(ServerAccent accent)
    {
        var waited = await _harness.Clock.FireNextTimerAsync(Cancellation);
        await accent.Reading;
        return waited;
    }

    [Theory]
    [InlineData("#3b82f6", "#3b82f6")]
    [InlineData(" #3B82F6 ", "#3b82f6")]
    [InlineData("#38f", "#3388ff")]
    [InlineData("#ef4444", "#ef4444")]
    public void AHexAccent_IsTheServers(string accent, string primary)
    {
        Assert.Equal(primary, AccentPalette.From(accent).Primary.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("blue")]
    [InlineData("#12345")]
    [InlineData("red;} body{display:none")]
    [InlineData(SavedBranding.NoAccent)]
    public void NoAccent_OrAnythingButHex_IsTheDefault(string? accent)
    {
        Assert.Same(AccentPalette.Default, AccentPalette.From(accent));
    }

    [Fact]
    public void TheDefault_IsCinemaDarksOwnPrimary_WithItsDarkLabel()
    {
        Assert.Equal("#e8e8ec", AccentPalette.Default.Primary.ToString());
        Assert.Equal("#141417", AccentPalette.Default.Label.ToString());
        Assert.True(AccentPalette.Default.IsDefault);
    }

    [Fact]
    public void AServersRed_IsItsOwnAccent_NotTheDefault()
    {
        var red = AccentPalette.From("#ef4444");

        Assert.False(red.IsDefault);
        Assert.Equal(AccentPalette.DarkLabel, red.Label);
    }

    [Fact]
    public void ADarkAccent_TakesAWhiteLabel_AndALightOneTheDarkLabel()
    {
        Assert.Equal(AccentPalette.White, AccentPalette.From("#1d4ed8").Label);
        Assert.Equal(AccentPalette.DarkLabel, AccentPalette.From("#facc15").Label);
    }

    [Theory]
    [InlineData("#1d4ed8")]
    [InlineData("#e8e8ec")]
    [InlineData("#ffffff")]
    public void HoverAndPress_AreTheAccentSeenThroughOverThePage_AsTheWebsPrimary90(string accent)
    {
        var palette = AccentPalette.From(accent);

        Assert.Equal(palette.Primary.Mix(AccentPalette.Page, 0.1), palette.Hover);
        Assert.Equal(palette.Primary.Mix(AccentPalette.Page, 0.2), palette.Pressed);
        Assert.NotEqual(palette.Primary, palette.Hover);
        Assert.True(palette.Pressed.Luminance < palette.Hover.Luminance);
    }

    [Fact]
    public async Task TheServersAccent_IsReadWithoutASignIn_AndPainted()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK, """{ "server_name": "Elena's Silo", "accent_color": "#1d4ed8" }"""));
        var accent = Create();

        await accent.StartAsync();

        Assert.Equal("#1d4ed8", accent.Palette.Primary.ToString());
        Assert.Equal(SiloAuth.Anonymous, Assert.Single(_harness.Silo.SentTo(HttpMethod.Get, BrandingPath)).Auth);
    }

    [Fact]
    public async Task TheServersWordmark_IsItsAddressOnTheServer()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "server_name": "Elena's Silo", "wordmark_url": "/api/v2/branding/assets/wordmark?v=1.webp" }"""));
        var accent = Create();

        await accent.StartAsync();

        Assert.Equal(ScriptedSilo.Base.AbsoluteUri.TrimEnd('/') + "/api/v2/branding/assets/wordmark?v=1.webp", accent.Wordmark?.AbsoluteUri);
    }

    [Theory]
    [InlineData("""{ "server_name": "Elena's Silo" }""")]
    [InlineData("""{ "wordmark_url": "" }""")]
    [InlineData("""{ "wordmark_url": "file:///C:/Windows/win.ini" }""")]
    [InlineData("""{ "wordmark_url": "//elsewhere.example/wordmark.webp" }""")]
    [InlineData("""{ "wordmark_url": "https://elsewhere.example/wordmark.webp" }""")]
    public async Task NoWordmark_OrOneFromElsewhere_LeavesTheServersName(string branding)
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK, branding));
        var accent = Create();

        await accent.StartAsync();

        Assert.Null(accent.Wordmark);
    }

    [Fact]
    public async Task AWordmarkGivenInFullOnTheServerItself_IsKept()
    {
        string full = ScriptedSilo.Base.AbsoluteUri.TrimEnd('/') + "/api/v2/branding/assets/wordmark?v=1.webp";
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK, $$"""{ "wordmark_url": "{{full}}" }"""));
        var accent = Create();

        await accent.StartAsync();

        Assert.Equal(full, accent.Wordmark?.AbsoluteUri);
    }

    [Fact]
    public async Task AFailedRead_ShowsNoWordmark()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.InternalServerError, "{}"));
        var accent = Create();

        await accent.StartAsync();

        Assert.Null(accent.Wordmark);
    }

    [Fact]
    public async Task AnotherServer_DoesNotKeepTheLastOnesWordmark_WhileItsOwnIsRead()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK, """{ "wordmark_url": "/first.webp" }"""));
        var accent = Create();
        await accent.StartAsync();
        Assert.NotNull(accent.Wordmark);
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _harness.Silo.On(HttpMethod.Get, BrandingPath, () => held.Task);

        _harness.Session.ActiveServer = _harness.Session.ActiveServer! with { Id = "another-server" };
        _harness.Session.RaiseChanged();

        Assert.Null(accent.Wordmark);
        held.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{ "wordmark_url": "/second.webp" }""", System.Text.Encoding.UTF8, "application/json"),
        });
        await accent.Reading;
        Assert.EndsWith("/second.webp", accent.Wordmark?.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedRead_IsTheWebsLightDefault()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Problem(500, "internal_error"));
        var accent = Create();

        await accent.StartAsync();

        Assert.Same(AccentPalette.Default, accent.Palette);
    }

    [Fact]
    public async Task AnotherServer_IsReadAgain_AndTheSameOneNot()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK, """{ "accent_color": "#1d4ed8" }"""));
        var other = ScriptedSilo.Entry("srv2", new Uri("https://other.example/silo"));
        _harness.Silo.On(HttpMethod.Get, ScriptedSilo.Url(BrandingPath, other.BaseUri),
            ScriptedSilo.Json(HttpStatusCode.OK, """{ "accent_color": "#16a34a" }"""));
        var accent = Create();
        await accent.StartAsync();

        _harness.Session.RaiseChanged();
        await accent.Reading;
        Assert.Single(_harness.Silo.SentTo(HttpMethod.Get, BrandingPath));

        _harness.Session.ActiveServer = other;
        _harness.Session.RaiseChanged();
        await accent.Reading;
        Assert.Equal("#16a34a", accent.Palette.Primary.ToString());
    }

    [Fact]
    public async Task NoServer_IsTheWebsLightDefault()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK, """{ "accent_color": "#1d4ed8" }"""));
        var accent = Create();
        await accent.StartAsync();

        _harness.Session.ActiveServer = null;
        _harness.Session.RaiseChanged();
        await accent.Reading;

        Assert.Same(AccentPalette.Default, accent.Palette);
    }

    [Fact]
    public async Task AServerThatSetRed_IsPaintedRed()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK, """{ "accent_color": "#ef4444" }"""));
        var accent = Create();

        await accent.StartAsync();

        Assert.Equal("#ef4444", accent.Palette.Primary.ToString());
        Assert.Equal(new SavedBranding("#ef4444", null), Remembered());
    }

    [Fact]
    public async Task AFailedRead_IsTriedAgain_AndItsAnswerPainted()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.NetworkError(),
            ScriptedSilo.Json(HttpStatusCode.OK, """{ "accent_color": "#1d4ed8", "wordmark_url": "/wordmark.webp" }"""));
        var accent = Create();
        await accent.StartAsync();
        Assert.Same(AccentPalette.Default, accent.Palette);

        await RetryAsync(accent);

        Assert.Equal("#1d4ed8", accent.Palette.Primary.ToString());
        Assert.EndsWith("/wordmark.webp", accent.Wordmark?.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(2, Reads());
    }

    [Fact]
    public async Task FailedReads_AreTriedAgainAfter5s_15s_AMinute_ThenEvery5Minutes()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Problem(503, "unavailable"));
        var accent = Create();
        await accent.StartAsync();

        List<TimeSpan> waits = [];
        for (int i = 0; i < 5; i++)
        {
            waits.Add(await RetryAsync(accent));
        }

        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)], waits);
        Assert.Equal(6, Reads());
        Assert.Same(AccentPalette.Default, accent.Palette);
    }

    [Theory]
    [InlineData(Blue)]
    [InlineData(NoAccent)]
    public async Task AnAnswer_WithOrWithoutAnAccent_IsNotTriedAgain(string branding)
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.NetworkError(), ScriptedSilo.Json(HttpStatusCode.OK, branding));
        var accent = Create();
        await accent.StartAsync();
        await RetryAsync(accent);

        _harness.Clock.Advance(TimeSpan.FromHours(1));
        _network.RaiseChanged();
        _harness.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Null(_harness.Clock.NextDue);
        Assert.Equal(2, Reads());
    }

    [Fact]
    public async Task ARead_ThatAnswersAtOnce_SetsNoRetry()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK, NoAccent));
        var accent = Create();

        await accent.StartAsync();

        Assert.Null(_harness.Clock.NextDue);
        Assert.Same(AccentPalette.Default, accent.Palette);
    }

    [Fact]
    public async Task AnotherServer_DuringTheWait_EndsTheRetries_AndStartsItsOwnFromTheFirstDelay()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.NetworkError());
        var other = ScriptedSilo.Entry("srv2", new Uri("https://other.example/silo"));
        _harness.Silo.On(HttpMethod.Get, ScriptedSilo.Url(BrandingPath, other.BaseUri), ScriptedSilo.NetworkError(),
            ScriptedSilo.Json(HttpStatusCode.OK, """{ "accent_color": "#16a34a" }"""));
        var accent = Create();
        await accent.StartAsync();
        await RetryAsync(accent);
        Assert.Equal(TimeSpan.FromSeconds(15), _harness.Clock.NextDue);

        _harness.Session.ActiveServer = other;
        _harness.Session.RaiseChanged();
        await accent.Reading;

        Assert.Equal(TimeSpan.FromSeconds(5), await RetryAsync(accent));
        Assert.Equal("#16a34a", accent.Palette.Primary.ToString());
        _harness.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(2, Reads());
        Assert.Equal(2, Reads(other.BaseUri));
    }

    [Fact]
    public async Task Disposing_EndsTheRetries()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.NetworkError());
        var accent = Create();
        await accent.StartAsync();
        Assert.NotNull(_harness.Clock.NextDue);

        accent.Dispose();
        _harness.Clock.Advance(TimeSpan.FromHours(1));
        _harness.Session.State = SessionState.SignedOut;
        _harness.Session.RaiseChanged();
        _harness.Session.State = SessionState.Ready;
        _harness.Session.RaiseChanged();

        Assert.Null(_harness.Clock.NextDue);
        Assert.False(_network.IsFollowed);
        Assert.Equal(1, Reads());
    }

    [Theory]
    [InlineData(SessionState.SignedOut, SessionState.ProfileRequired)]
    [InlineData(SessionState.SignedOut, SessionState.Ready)]
    [InlineData(SessionState.ProfileRequired, SessionState.Ready)]
    [InlineData(SessionState.PasswordChangeRequired, SessionState.Ready)]
    public async Task ASignInOrASessionThatBecomesReady_TriesAgainAtOnce(SessionState before, SessionState after)
    {
        _harness.Session.State = before;
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.NetworkError(), ScriptedSilo.Json(HttpStatusCode.OK, Blue));
        var accent = Create();
        await accent.StartAsync();

        _harness.Session.State = after;
        _harness.Session.RaiseChanged();
        await accent.Reading;

        Assert.Equal("#1d4ed8", accent.Palette.Primary.ToString());
        Assert.Equal(2, Reads());
        Assert.Null(_harness.Clock.NextDue);
    }

    [Theory]
    [InlineData(SessionState.Ready, SessionState.Ready)]
    [InlineData(SessionState.Ready, SessionState.SignedOut)]
    [InlineData(SessionState.Ready, SessionState.ProfileRequired)]
    public async Task OtherSessionChanges_WaitForTheRetry(SessionState before, SessionState after)
    {
        _harness.Session.State = before;
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.NetworkError());
        var accent = Create();
        await accent.StartAsync();

        _harness.Session.State = after;
        _harness.Session.RaiseChanged();
        await accent.Reading;

        Assert.Equal(1, Reads());
        Assert.Equal(TimeSpan.FromSeconds(5), _harness.Clock.NextDue);
    }

    [Fact]
    public async Task TheNetworkChanging_TriesAgainOnceItSettles_AndTheWaitsStartOver()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.NetworkError());
        var accent = Create();
        await accent.StartAsync();
        await RetryAsync(accent);
        await RetryAsync(accent);
        Assert.Equal(TimeSpan.FromMinutes(1), _harness.Clock.NextDue);

        // A change comes in bursts: an adapter going down, another coming up.
        _network.RaiseChanged();
        _network.RaiseChanged();

        Assert.Equal(TimeSpan.FromSeconds(2), await RetryAsync(accent));
        Assert.Equal(4, Reads());
        Assert.Equal(TimeSpan.FromSeconds(5), _harness.Clock.NextDue);
    }

    [Fact]
    public async Task TheNetworkChanging_ReadsNothing_WhenNothingFailed()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK, Blue));
        var accent = Create();
        await accent.StartAsync();

        _network.RaiseChanged();

        Assert.Null(_harness.Clock.NextDue);
        Assert.Equal(1, Reads());
    }

    [Fact]
    public async Task AStreakOfFailures_IsLoggedOnce_WithoutTheServersAddress()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.NetworkError());
        var accent = Create();
        await accent.StartAsync();

        for (int i = 0; i < 4; i++)
        {
            await RetryAsync(accent);
        }

        Assert.Single(_harness.Logs.Lines, line => line.Contains("Could not read the server's branding", StringComparison.Ordinal));
        Assert.DoesNotContain(ScriptedSilo.Base.Host, _harness.Logs.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheRememberedAccentAndWordmark_ArePaintedAtOnce_BeforeTheServerAnswers()
    {
        Remember(new SavedBranding("#16a34a", WordmarkUrl));
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _harness.Silo.On(HttpMethod.Get, BrandingPath, () => held.Task);
        var accent = Create();

        var reading = accent.StartAsync();

        Assert.Equal("#16a34a", accent.Palette.Primary.ToString());
        Assert.Equal(WordmarkUrl, accent.Wordmark?.AbsoluteUri);
        held.SetResult(await ScriptedSilo.Json(HttpStatusCode.OK, Blue)());
        await reading;
        Assert.Equal("#1d4ed8", accent.Palette.Primary.ToString());
    }

    [Fact]
    public async Task AFailedRead_KeepsTheRememberedAccent_AndTriesAgain()
    {
        Remember(new SavedBranding("#16a34a", WordmarkUrl));
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.NetworkError());
        var accent = Create();

        await accent.StartAsync();
        await RetryAsync(accent);

        Assert.Equal("#16a34a", accent.Palette.Primary.ToString());
        Assert.Equal(WordmarkUrl, accent.Wordmark?.AbsoluteUri);
        Assert.Equal(new SavedBranding("#16a34a", WordmarkUrl), Remembered());
        Assert.Equal(TimeSpan.FromSeconds(15), _harness.Clock.NextDue);
    }

    [Fact]
    public async Task ARemembered_None_IsTheWebsLightDefault()
    {
        Remember(new SavedBranding(SavedBranding.NoAccent, null));
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.NetworkError());
        var accent = Create();

        await accent.StartAsync();

        Assert.Same(AccentPalette.Default, accent.Palette);
        Assert.Null(accent.Wordmark);
    }

    [Theory]
    [InlineData("https://elsewhere.example/wordmark.webp")]
    [InlineData("https://nas.example:8443/silo/api/v2/branding/assets/wordmark.webp")]
    [InlineData("http://nas.example/silo/api/v2/branding/assets/wordmark.webp")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("/api/v2/branding/assets/wordmark.webp")]
    [InlineData("not a url")]
    public async Task ARememberedWordmark_FromElsewhere_IsNotShown(string wordmark)
    {
        Remember(new SavedBranding("#16a34a", wordmark));
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.NetworkError());
        var accent = Create();

        await accent.StartAsync();

        Assert.Null(accent.Wordmark);
        Assert.Equal("#16a34a", accent.Palette.Primary.ToString());
    }

    [Fact]
    public async Task ARead_IsRemembered_WithTheAccentNormalized_AndTheWordmarksAddress()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "accent_color": " #38F ", "wordmark_url": "/api/v2/branding/assets/wordmark?v=1.webp" }"""));
        var accent = Create();

        await accent.StartAsync();

        Assert.Equal(new SavedBranding("#3388ff", WordmarkUrl), Remembered());
    }

    [Theory]
    [InlineData("""{ "accent_color": "#1d4ed8" }""", "#1d4ed8")]
    [InlineData("""{ "server_name": "Elena's Silo" }""", SavedBranding.NoAccent)]
    [InlineData("""{ "accent_color": "blue" }""", SavedBranding.NoAccent)]
    public async Task AChangedAccent_ReplacesTheRememberedOne(string branding, string remembered)
    {
        Remember(new SavedBranding("#16a34a", WordmarkUrl));
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK, branding));
        var accent = Create();

        await accent.StartAsync();

        Assert.Equal(new SavedBranding(remembered, null), Remembered());
        Assert.Equal(AccentPalette.From(remembered), accent.Palette);
        Assert.Null(accent.Wordmark);
    }

    [Fact]
    public async Task TheSameAnswer_SavesNothing()
    {
        Remember(new SavedBranding("#1d4ed8", null));
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK, Blue));
        int changes = 0;
        _registry.Changed += (_, _) => changes++;
        var accent = Create();

        await accent.StartAsync();

        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task AnotherServer_IsPaintedWithWhatItRemembers_AndAFailedReadThereKeepsIt()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK, Blue));
        var other = ScriptedSilo.Entry("srv2", new Uri("https://other.example/silo")) with { Branding = new SavedBranding("#16a34a", null) };
        _registry.Add(other);
        _harness.Silo.On(HttpMethod.Get, ScriptedSilo.Url(BrandingPath, other.BaseUri), ScriptedSilo.NetworkError());
        var accent = Create();
        await accent.StartAsync();

        _harness.Session.ActiveServer = other;
        _harness.Session.RaiseChanged();
        await accent.Reading;

        Assert.Equal("#16a34a", accent.Palette.Primary.ToString());
        Assert.Equal(new SavedBranding("#1d4ed8", null), Remembered());
        Assert.Equal(new SavedBranding("#16a34a", null), Remembered("srv2"));
    }

    [Fact]
    public async Task TheAddressAnsweringAsAnotherServer_IsReadAgainAtOnce()
    {
        Remember(new SavedBranding("#16a34a", WordmarkUrl));
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK, Blue),
            ScriptedSilo.Json(HttpStatusCode.OK, """{ "accent_color": "#facc15" }"""));
        var accent = Create();
        await accent.StartAsync();
        Assert.Equal("#1d4ed8", accent.Palette.Primary.ToString());

        // Added again, the address answers as another server: its saved branding went with the old one.
        var another = ScriptedSilo.Server with { ServerId = "server-another" };
        _registry.Add(another);
        _harness.Session.ActiveServer = another;
        _harness.Session.RaiseChanged();
        await accent.Reading;

        Assert.Equal(2, Reads());
        Assert.Equal("#facc15", accent.Palette.Primary.ToString());
        Assert.Equal(new SavedBranding("#facc15", null), Remembered());
    }

    [Fact]
    public async Task TheSavedBrandingGone_ReadsTheServerAgain()
    {
        Remember(new SavedBranding("#16a34a", null));
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK, Blue));
        var accent = Create();
        await accent.StartAsync();

        _harness.Session.ActiveServer = ScriptedSilo.Server;
        _harness.Session.RaiseChanged();
        await accent.Reading;

        Assert.Equal(2, Reads());
    }

    [Fact]
    public async Task AnAnswerFromBeforeTheAddressAnsweredAsAnotherServer_IsNotSaved()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK, Blue));
        var hold = _registry.HoldNextUpdate();
        var accent = Create();

        var reading = accent.StartAsync();
        await hold.Reached.Task.WaitAsync(Cancellation);
        _registry.Add(ScriptedSilo.Server with { ServerId = "server-another" });
        hold.Release();
        await reading;

        Assert.Null(Remembered());
    }

    // The answer to a read that was replaced, held until the newer one has answered.
    private static Func<Task<HttpResponseMessage>> Held(TaskCompletionSource release) => async () =>
    {
        await release.Task;
        return await ScriptedSilo.Json(HttpStatusCode.OK, """{ "accent_color": "#facc15" }""")();
    };

    [Fact]
    public async Task AnAnswerFromBeforeTheServerWasLeftAndChosenAgain_IsDropped()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _harness.Silo.On(HttpMethod.Get, BrandingPath, Held(release), ScriptedSilo.Json(HttpStatusCode.OK, Blue));
        var other = ScriptedSilo.Entry("srv2", new Uri("https://other.example/silo"));
        _harness.Silo.On(HttpMethod.Get, ScriptedSilo.Url(BrandingPath, other.BaseUri), ScriptedSilo.Json(HttpStatusCode.OK, """{ "accent_color": "#16a34a" }"""));
        var accent = Create();
        var first = accent.StartAsync();

        _harness.Session.ActiveServer = other;
        _harness.Session.RaiseChanged();
        await accent.Reading;
        _harness.Session.ActiveServer = ScriptedSilo.Server;
        _harness.Session.RaiseChanged();
        await accent.Reading;
        release.SetResult();
        await first;

        Assert.Equal("#1d4ed8", accent.Palette.Primary.ToString());
        Assert.Equal(new SavedBranding("#1d4ed8", null), Remembered());
    }

    [Fact]
    public async Task AnAnswerToARetry_ThatASignInReadAgain_IsDropped()
    {
        _harness.Session.State = SessionState.ProfileRequired;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.NetworkError(), Held(release), ScriptedSilo.Json(HttpStatusCode.OK, Blue));
        var accent = Create();
        await accent.StartAsync();
        await _harness.Clock.FireNextTimerAsync(Cancellation);
        var retry = accent.Reading;

        _harness.Session.State = SessionState.Ready;
        _harness.Session.RaiseChanged();
        await accent.Reading;
        release.SetResult();
        await retry;

        Assert.Equal(3, Reads());
        Assert.Equal("#1d4ed8", accent.Palette.Primary.ToString());
        Assert.Equal(new SavedBranding("#1d4ed8", null), Remembered());
    }

    [Fact]
    public async Task AnAnswerAfterDisposing_IsDropped()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _harness.Silo.On(HttpMethod.Get, BrandingPath, Held(release));
        var accent = Create();
        var reading = accent.StartAsync();

        accent.Dispose();
        release.SetResult();
        await reading;

        Assert.Same(AccentPalette.Default, accent.Palette);
        Assert.Null(Remembered());
        Assert.Null(_harness.Clock.NextDue);
    }

    [Fact]
    public async Task AnUnexpectedFailure_IsTriedAgainToo_AndLoggedWithoutTheAddress()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath,
            () => throw new InvalidOperationException("Something broke at " + ScriptedSilo.Base),
            ScriptedSilo.Json(HttpStatusCode.OK, Blue));
        var accent = Create();

        await accent.StartAsync();

        Assert.Equal(TimeSpan.FromSeconds(5), _harness.Clock.NextDue);
        Assert.Contains(_harness.Logs.Lines, line => line.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(ScriptedSilo.Base.Host, _harness.Logs.All, StringComparison.OrdinalIgnoreCase);
        await RetryAsync(accent);
        Assert.Equal("#1d4ed8", accent.Palette.Primary.ToString());
    }

    [Fact]
    public async Task AFailedSave_KeepsTheAccent_AndIsLogged()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK, Blue));
        _registry.FailWith = new IOException("The disk is full.");
        var accent = Create();

        await accent.StartAsync();

        Assert.Equal("#1d4ed8", accent.Palette.Primary.ToString());
        Assert.Contains(_harness.Logs.Lines, line => line.Contains("Could not remember the server's branding", StringComparison.Ordinal));
    }
}
