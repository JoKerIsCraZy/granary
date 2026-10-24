using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.Profiles;

public sealed class ProfilePickerViewModelTests
{
    private const string ProfilesPath = "api/v2/profiles";
    private const string KidPinPath = "api/v2/profiles/p-kid/verify-pin";
    private const string Pin = "4321";

    private const string ProfilesJson = """
        {
          "items": [
            {"id": "p-owner", "name": "Laura Palmer", "is_primary": true, "has_pin": false, "is_child": false,
             "avatar": "preset:fox", "avatar_source": "preset", "avatar_url": "/avatars/presets/fox.png",
             "language": "en", "subtitle_language": "", "subtitle_mode": "auto", "max_advisory_age": null},
            {"id": "p-kid", "name": "tom", "is_primary": false, "has_pin": true, "is_child": true,
             "avatar": "", "avatar_source": "none", "language": "en", "subtitle_language": "", "subtitle_mode": "auto"},
            {"id": "p-guest", "name": "Guest", "is_primary": false, "has_pin": false, "is_child": false,
             "avatar": "upload", "avatar_source": "upload", "avatar_url": "https://s3.example/avatars/g.webp?X-Amz-Signature=abc",
             "language": "de", "subtitle_language": "de", "subtitle_mode": "always"}
          ],
          "avatar_upload_enabled": true
        }
        """;

    private const string KidWithoutPinJson = """
        {"items": [{"id": "p-kid", "name": "tom", "is_primary": false, "has_pin": false, "is_child": true,
          "avatar": "", "avatar_source": "none", "language": "en", "subtitle_language": "", "subtitle_mode": "auto"}],
         "avatar_upload_enabled": false}
        """;

    private const string ValidPinJson = """{"valid": true, "profile_token": "pvt_kid", "expires_at": null}""";
    private const string WrongPinJson = """{"valid": false, "expires_at": null}""";

    /// <summary>Holds posted work until the test runs it, like a busy UI thread.</summary>
    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _queue = new();

        public bool HasThreadAccess => true;

        public void Post(Action action) => _queue.Enqueue(action);

        public void RunPending()
        {
            while (_queue.TryDequeue(out var action))
            {
                action();
            }
        }
    }

    private sealed class Harness : IDisposable
    {
        public Harness(IUiDispatcher? dispatcher = null)
        {
            Recorder = new ProfileSelectionRecorder(Session);
            ViewModel = new ProfilePickerViewModel(new ProfilesApi(Silo.Http), Recorder, dispatcher ?? new ImmediateDispatcher(), Logger);
            Silo.On(HttpMethod.Get, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.OK, ProfilesJson));
        }

        public ScriptedSilo Silo { get; } = new();

        public FakeSessionManager Session { get; } = new() { ActiveServer = ScriptedSilo.Server, State = SessionState.ProfileRequired };

        public ProfileSelectionRecorder Recorder { get; }

        public RecordingLogger<ProfilePickerViewModel> Logger { get; } = new();

        public ProfilePickerViewModel ViewModel { get; }

        public ProfileItemViewModel Profile(string id) => ViewModel.Profiles.Single(p => p.Id == id);

        /// <summary>Loads the list and opens the PIN prompt of the child profile.</summary>
        public async Task OpenKidPromptAsync()
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
            await ViewModel.SelectCommand.ExecuteAsync(Profile("p-kid"));
        }

        public void Dispose() => ViewModel.Dispose();
    }

    [Fact]
    public async Task Load_MapsTheProfilesAndResolvesAvatarUrls()
    {
        using var harness = new Harness();

        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal(["p-owner", "p-kid", "p-guest"], harness.ViewModel.Profiles.Select(p => p.Id));
        var owner = harness.Profile("p-owner");
        Assert.Equal("Laura Palmer", owner.Name);
        Assert.Equal("LP", owner.Initials);
        Assert.True(owner.IsPrimary);
        Assert.False(owner.HasPin);
        Assert.Equal("https://nas.example/silo/avatars/presets/fox.png", owner.AvatarUrl?.AbsoluteUri);
        var kid = harness.Profile("p-kid");
        Assert.True(kid.HasPin);
        Assert.True(kid.IsChild);
        Assert.Null(kid.AvatarUrl);
        Assert.Equal("T", kid.Initials);
        Assert.Equal("https://s3.example/avatars/g.webp?X-Amz-Signature=abc", harness.Profile("p-guest").AvatarUrl?.AbsoluteUri);

        var request = Assert.Single(harness.Silo.Sent);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://nas.example/silo/api/v2/profiles", request.Uri.AbsoluteUri);
        Assert.Equal(SiloAuth.Account, request.Auth);
        Assert.False(harness.ViewModel.IsLoading);
        Assert.False(harness.ViewModel.HasLoadError);
        Assert.Equal("", harness.ViewModel.Status);
    }

    [Fact]
    public async Task Select_ProfileWithoutPin_SelectsItWithoutAToken()
    {
        using var harness = new Harness();
        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        await harness.ViewModel.SelectCommand.ExecuteAsync(harness.Profile("p-owner"));

        Assert.Equal(["profile:p-owner:no-token"], harness.Session.Calls);
        Assert.Equal([null], harness.Recorder.ProfileTokens);
        Assert.False(harness.ViewModel.IsPinPromptOpen);
        Assert.Single(harness.Silo.Sent);
    }

    [Fact]
    public async Task Select_ProfileWithPin_OpensThePromptWithoutCallingTheServer()
    {
        using var harness = new Harness();

        await harness.OpenKidPromptAsync();

        Assert.True(harness.ViewModel.IsPinPromptOpen);
        Assert.Equal("p-kid", harness.ViewModel.PinProfile?.Id);
        Assert.Equal("Enter the PIN for tom", harness.ViewModel.PinPromptTitle);
        Assert.Empty(harness.Session.Calls);
        Assert.Single(harness.Silo.Sent);
    }

    [Fact]
    public async Task SubmitPin_ValidPin_SelectsTheProfileWithItsToken()
    {
        using var harness = new Harness();
        harness.Silo.On(HttpMethod.Post, KidPinPath, ScriptedSilo.Json(HttpStatusCode.OK, ValidPinJson));
        await harness.OpenKidPromptAsync();

        await harness.ViewModel.SubmitPinCommand.ExecuteAsync(Pin);

        var verify = Assert.Single(harness.Silo.SentTo(HttpMethod.Post, KidPinPath));
        Assert.Equal("https://nas.example/silo/api/v2/profiles/p-kid/verify-pin", verify.Uri.AbsoluteUri);
        Assert.DoesNotContain(Pin, verify.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("""{"pin":"4321"}""", verify.Body);
        Assert.Equal(SiloAuth.Account, verify.Auth);
        Assert.Equal(["pvt_kid"], harness.Recorder.ProfileTokens);
        Assert.Equal(["profile:p-kid:token"], harness.Session.Calls);
        Assert.False(harness.ViewModel.IsPinPromptOpen);
        Assert.False(harness.ViewModel.IsVerifyingPin);
        Assert.Null(harness.ViewModel.PinProfile);
    }

    [Fact]
    public async Task SubmitPin_WrongPin_KeepsThePromptOpenWithAMessage()
    {
        using var harness = new Harness();
        harness.Silo.On(HttpMethod.Post, KidPinPath, ScriptedSilo.Json(HttpStatusCode.OK, WrongPinJson));
        await harness.OpenKidPromptAsync();

        await harness.ViewModel.SubmitPinCommand.ExecuteAsync(Pin);

        Assert.Single(harness.Silo.SentTo(HttpMethod.Post, KidPinPath));
        Assert.Equal("That PIN is not right.", harness.ViewModel.PinError);
        Assert.True(harness.ViewModel.IsPinPromptOpen);
        Assert.False(harness.ViewModel.IsVerifyingPin);
        Assert.Empty(harness.Session.Calls);
    }

    [Theory]
    [InlineData(240, "Too many attempts. Try again in 4 minutes.")]
    [InlineData(61, "Too many attempts. Try again in 2 minutes.")]
    [InlineData(20, "Too many attempts. Try again in 1 minute.")]
    [InlineData(-1, "Too many attempts. Try again in a few minutes.")]
    public async Task SubmitPin_Lockout_ShowsWhenToTryAgain(int retryAfterSeconds, string expected)
    {
        using var harness = new Harness();
        TimeSpan? retryAfter = retryAfterSeconds < 0 ? null : TimeSpan.FromSeconds(retryAfterSeconds);
        harness.Silo.On(HttpMethod.Post, KidPinPath, ScriptedSilo.Problem(429, ProblemCodes.RateLimited, retryAfter));
        await harness.OpenKidPromptAsync();

        await harness.ViewModel.SubmitPinCommand.ExecuteAsync(Pin);

        Assert.Single(harness.Silo.SentTo(HttpMethod.Post, KidPinPath));
        Assert.Equal(expected, harness.ViewModel.PinError);
        Assert.True(harness.ViewModel.IsPinPromptOpen);
        Assert.Empty(harness.Session.Calls);
    }

    [Fact]
    public async Task SubmitPin_ServerBusy_AsksToTryAgainAndDoesNotRetry()
    {
        using var harness = new Harness();
        harness.Silo.On(HttpMethod.Post, KidPinPath,
            ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable, TimeSpan.FromSeconds(1)),
            ScriptedSilo.Json(HttpStatusCode.OK, ValidPinJson));
        await harness.OpenKidPromptAsync();

        await harness.ViewModel.SubmitPinCommand.ExecuteAsync(Pin);

        Assert.Single(harness.Silo.SentTo(HttpMethod.Post, KidPinPath));
        Assert.Equal("The server is busy, try again.", harness.ViewModel.PinError);
        Assert.Empty(harness.Session.Calls);
    }

    [Fact]
    public async Task SubmitPin_NetworkError_ShowsAFriendlyMessage()
    {
        using var harness = new Harness();
        harness.Silo.On(HttpMethod.Post, KidPinPath, ScriptedSilo.NetworkError());
        await harness.OpenKidPromptAsync();

        await harness.ViewModel.SubmitPinCommand.ExecuteAsync(Pin);

        Assert.Single(harness.Silo.SentTo(HttpMethod.Post, KidPinPath));
        Assert.Equal("Could not reach the server. Check the connection and try again.", harness.ViewModel.PinError);
        Assert.True(harness.ViewModel.IsPinPromptOpen);
    }

    [Fact]
    public async Task SubmitPin_ProfileNoLongerHasAPin_ReloadsTheList()
    {
        using var harness = new Harness();
        harness.Silo.On(HttpMethod.Get, ProfilesPath,
            ScriptedSilo.Json(HttpStatusCode.OK, ProfilesJson),
            ScriptedSilo.Json(HttpStatusCode.OK, KidWithoutPinJson));
        harness.Silo.On(HttpMethod.Post, KidPinPath, ScriptedSilo.Problem(404, ProblemCodes.NotFound));
        await harness.OpenKidPromptAsync();

        await harness.ViewModel.SubmitPinCommand.ExecuteAsync(Pin);

        Assert.Single(harness.Silo.SentTo(HttpMethod.Post, KidPinPath));
        Assert.Equal(2, harness.Silo.SentTo(HttpMethod.Get, ProfilesPath).Count);
        Assert.False(harness.ViewModel.IsPinPromptOpen);
        Assert.False(Assert.Single(harness.ViewModel.Profiles).HasPin);
        Assert.Equal("That profile has changed. Choose it again.", harness.ViewModel.Status);
        Assert.Empty(harness.Session.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234567890123")]
    public async Task SubmitPin_EmptyOrOverlongPin_DoesNotCallTheServer(string pin)
    {
        using var harness = new Harness();
        await harness.OpenKidPromptAsync();

        await harness.ViewModel.SubmitPinCommand.ExecuteAsync(pin);

        Assert.Empty(harness.Silo.SentTo(HttpMethod.Post, KidPinPath));
        Assert.NotEqual("", harness.ViewModel.PinError);
        Assert.True(harness.ViewModel.IsPinPromptOpen);
    }

    [Fact]
    public async Task CancelPin_WhileTheServerChecks_DiscardsTheAnswer()
    {
        using var harness = new Harness();
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Silo.On(HttpMethod.Post, KidPinPath, () => answer.Task);
        await harness.OpenKidPromptAsync();

        var submit = harness.ViewModel.SubmitPinCommand.ExecuteAsync(Pin);
        Assert.True(harness.ViewModel.IsVerifyingPin);
        harness.ViewModel.CancelPinCommand.Execute(null);
        answer.SetResult(await ScriptedSilo.Json(HttpStatusCode.OK, ValidPinJson)());
        await submit;

        Assert.Empty(harness.Session.Calls);
        Assert.False(harness.ViewModel.IsPinPromptOpen);
        Assert.False(harness.ViewModel.IsVerifyingPin);
        Assert.Equal("", harness.ViewModel.PinError);
    }

    [Fact]
    public async Task SubmitPin_NeverLogsThePin()
    {
        using var harness = new Harness();
        harness.Silo.On(HttpMethod.Post, KidPinPath,
            ScriptedSilo.Problem(429, ProblemCodes.RateLimited, TimeSpan.FromMinutes(5)),
            ScriptedSilo.Json(HttpStatusCode.OK, ValidPinJson));
        await harness.OpenKidPromptAsync();

        await harness.ViewModel.SubmitPinCommand.ExecuteAsync("97531");
        await harness.ViewModel.SubmitPinCommand.ExecuteAsync("97531");

        Assert.Equal(2, harness.Silo.SentTo(HttpMethod.Post, KidPinPath).Count);
        Assert.NotEmpty(harness.Logger.Lines);
        Assert.DoesNotContain(harness.Logger.Lines, line => line.Contains("97531", StringComparison.Ordinal));
        Assert.Equal(["profile:p-kid:token"], harness.Session.Calls);
    }

    [Theory]
    [InlineData("error", "The server could not list the profiles (error 500).")]
    [InlineData("busy", "The server is busy, try again.")]
    [InlineData("rate-limited", "The server is receiving too many requests. Try again in 2 minutes.")]
    [InlineData("network", "Could not reach the server. Check the connection and try again.")]
    [InlineData("malformed", "The server sent an answer this app does not understand.")]
    public async Task Load_Failure_ShowsTheReasonAndRetryLoadsAgain(string failureKind, string expected)
    {
        using var harness = new Harness();
        var failure = failureKind switch
        {
            "error" => ScriptedSilo.Problem(500, "internal_error"),
            "busy" => ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable),
            "rate-limited" => ScriptedSilo.Problem(429, ProblemCodes.RateLimited, TimeSpan.FromSeconds(90)),
            "network" => ScriptedSilo.NetworkError(),
            _ => ScriptedSilo.Json(HttpStatusCode.OK, "{not json"),
        };
        harness.Silo.On(HttpMethod.Get, ProfilesPath, failure, ScriptedSilo.Json(HttpStatusCode.OK, ProfilesJson));

        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.HasLoadError);
        Assert.Equal(expected, harness.ViewModel.Status);
        Assert.Empty(harness.ViewModel.Profiles);
        Assert.False(harness.ViewModel.IsLoading);

        await harness.ViewModel.RetryCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.HasLoadError);
        Assert.Equal("", harness.ViewModel.Status);
        Assert.Equal(3, harness.ViewModel.Profiles.Count);
    }

    [Fact]
    public async Task Select_WhenTheSessionRefuses_ShowsAStatus()
    {
        using var harness = new Harness();
        await harness.ViewModel.LoadCommand.ExecuteAsync(null);
        harness.Session.FailWith = new InvalidOperationException("no session");

        await harness.ViewModel.SelectCommand.ExecuteAsync(harness.Profile("p-owner"));

        Assert.Equal("Could not open the profile. Try again.", harness.ViewModel.Status);
        Assert.False(harness.ViewModel.IsSelecting);
    }

    [Fact]
    public async Task ActiveServerChange_ClosesThePromptAndLoadsTheNewServersProfiles()
    {
        using var harness = new Harness();
        await harness.OpenKidPromptAsync();
        var other = ScriptedSilo.Entry("srv2", new Uri("https://other.example"));
        harness.Silo.On(HttpMethod.Get, ScriptedSilo.Url(ProfilesPath, other.BaseUri), ScriptedSilo.Json(HttpStatusCode.OK, KidWithoutPinJson));

        harness.Session.ActiveServer = other;
        harness.Session.RaiseChanged();
        await (harness.ViewModel.LoadCommand.ExecutionTask ?? Task.CompletedTask);

        Assert.False(harness.ViewModel.IsPinPromptOpen);
        Assert.Equal("https://other.example/api/v2/profiles", harness.Silo.Sent[^1].Uri.AbsoluteUri);
        Assert.False(Assert.Single(harness.ViewModel.Profiles).HasPin);
    }

    [Fact]
    public async Task Dispose_StopsFollowingTheSession()
    {
        using var harness = new Harness();
        await harness.ViewModel.LoadCommand.ExecuteAsync(null);
        harness.ViewModel.Dispose();

        harness.Session.ActiveServer = ScriptedSilo.Entry("srv2", new Uri("https://other.example"));
        harness.Session.RaiseChanged();

        Assert.Single(harness.Silo.Sent);
        Assert.Equal(3, harness.ViewModel.Profiles.Count);
    }

    [Fact]
    public async Task Select_DoubleClick_KeepsTheRunningSelection()
    {
        using var harness = new Harness();
        await harness.ViewModel.LoadCommand.ExecuteAsync(null);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Recorder.SelectGate = gate.Task;
        var owner = harness.Profile("p-owner");

        var first = harness.ViewModel.SelectCommand.ExecuteAsync(owner);
        var second = harness.ViewModel.SelectCommand.ExecuteAsync(owner);
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(["profile:p-owner:no-token"], harness.Session.Calls);
        Assert.Equal("", harness.ViewModel.Status);
    }

    [Fact]
    public async Task SubmitPin_AfterCancelAndReopen_IgnoresTheEarlierCheck()
    {
        using var harness = new Harness();
        var firstAnswer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Silo.On(HttpMethod.Post, KidPinPath, () => firstAnswer.Task, ScriptedSilo.Json(HttpStatusCode.OK, ValidPinJson));
        await harness.OpenKidPromptAsync();

        var first = harness.ViewModel.SubmitPinCommand.ExecuteAsync("1111");
        harness.ViewModel.CancelPinCommand.Execute(null);
        await harness.ViewModel.SelectCommand.ExecuteAsync(harness.Profile("p-kid"));
        await harness.ViewModel.SubmitPinCommand.ExecuteAsync(Pin);
        firstAnswer.SetResult(await ScriptedSilo.Json(HttpStatusCode.OK, WrongPinJson)());
        await first;

        Assert.Equal(2, harness.Silo.SentTo(HttpMethod.Post, KidPinPath).Count);
        Assert.Equal(["pvt_kid"], harness.Recorder.ProfileTokens);
        Assert.Equal("", harness.ViewModel.PinError);
    }

    [Fact]
    public async Task SubmitPin_ServerChangedDuringTheCheck_DoesNotApplyTheToken()
    {
        var dispatcher = new QueuedDispatcher();
        using var harness = new Harness(dispatcher);
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Silo.On(HttpMethod.Post, KidPinPath, () => answer.Task);
        await harness.OpenKidPromptAsync();

        var submit = harness.ViewModel.SubmitPinCommand.ExecuteAsync(Pin);
        harness.Session.ActiveServer = ScriptedSilo.Entry("srv2", new Uri("https://other.example"));
        harness.Session.RaiseChanged();
        answer.SetResult(await ScriptedSilo.Json(HttpStatusCode.OK, ValidPinJson)());
        await submit;

        Assert.Empty(harness.Recorder.ProfileTokens);
        Assert.Empty(harness.Session.Calls);
        Assert.False(harness.ViewModel.IsPinPromptOpen);
    }

    [Fact]
    public async Task ServerRemovedDuringALoad_StopsLoadingAndDropsTheAnswer()
    {
        using var harness = new Harness();
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Silo.On(HttpMethod.Get, ProfilesPath, () => answer.Task);

        var load = harness.ViewModel.LoadCommand.ExecuteAsync(null);
        Assert.True(harness.ViewModel.IsLoading);
        harness.Session.ActiveServer = null;
        harness.Session.State = SessionState.NoServer;
        harness.Session.RaiseChanged();
        answer.SetResult(await ScriptedSilo.Json(HttpStatusCode.OK, ProfilesJson)());
        await load;

        Assert.False(harness.ViewModel.IsLoading);
        Assert.Empty(harness.ViewModel.Profiles);
    }

    [Fact]
    public async Task ServerBecomesActive_AfterALoadWithoutServer_LoadsTheProfiles()
    {
        using var harness = new Harness();
        harness.Session.ActiveServer = null;
        await harness.ViewModel.LoadCommand.ExecuteAsync(null);
        Assert.Equal("No server is selected.", harness.ViewModel.Status);
        Assert.False(harness.ViewModel.IsLoading);

        harness.Session.ActiveServer = ScriptedSilo.Server;
        harness.Session.RaiseChanged();
        await (harness.ViewModel.LoadCommand.ExecutionTask ?? Task.CompletedTask);

        Assert.Equal(3, harness.ViewModel.Profiles.Count);
        Assert.Equal("", harness.ViewModel.Status);
    }
}
