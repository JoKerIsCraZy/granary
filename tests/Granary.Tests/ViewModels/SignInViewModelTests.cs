using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Auth;
using Granary.Core.Servers;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

public sealed class SignInViewModelTests
{
    private readonly FakeSessionManager _session = new();
    private readonly InMemoryServerRegistry _registry = new();
    private readonly RecordingNavigation _navigation = new();
    private readonly LogSink _logs = new();
    private string _providers = """{"items":[],"password_login":true}""";

    private SignInViewModel Create()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeSiloServer.Json(200, _providers)));
        return new SignInViewModel(_session, _registry, new AuthApi(new SiloHttp(new HttpClient(handler))), _navigation, _logs.For<SignInViewModel>());
    }

    private async Task<(SignInViewModel ViewModel, ServerEntry Server)> LoadedAsync()
    {
        var server = await _registry.AddOrUpdateAsync(new Uri("https://silo.test"), "Living room", null, TestContext.Current.CancellationToken);
        var viewModel = Create();
        await viewModel.LoadAsync(server.Id, TestContext.Current.CancellationToken);
        return (viewModel, server);
    }

    [Fact]
    public async Task Load_ShowsTheServerAndWhetherPasswordsWork()
    {
        _providers = """{"items":[{"id":"oidc","display_name":"SSO","mode":"redirect","default":true}],"password_login":false}""";

        var (viewModel, server) = await LoadedAsync();

        Assert.Equal(server, viewModel.Server);
        Assert.False(viewModel.IsPasswordSignInAvailable);
    }

    [Fact]
    public async Task Load_UnknownServer_SaysSo()
    {
        var viewModel = Create();

        await viewModel.LoadAsync("missing", TestContext.Current.CancellationToken);

        Assert.Null(viewModel.Server);
        Assert.Contains("no longer saved", viewModel.Status, StringComparison.Ordinal);
        Assert.False(viewModel.SignInCommand.CanExecute(null));
    }

    [Fact]
    public async Task SignIn_NeedsUsernameAndPassword()
    {
        var (viewModel, _) = await LoadedAsync();
        Assert.False(viewModel.SignInCommand.CanExecute(null));

        viewModel.Username = "alex";
        Assert.False(viewModel.SignInCommand.CanExecute(null));

        viewModel.Password = "pw";
        Assert.True(viewModel.SignInCommand.CanExecute(null));
    }

    [Fact]
    public async Task SignIn_PassesTrimmedUsernameAndClearsThePassword()
    {
        var (viewModel, server) = await LoadedAsync();
        viewModel.Username = "  alex ";
        viewModel.Password = "pw";

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.Equal($"password:{server.Id}:alex", Assert.Single(_session.Calls));
        Assert.Equal("", viewModel.Password);
        Assert.Equal("", viewModel.Status);
    }

    [Theory]
    [InlineData(401, ProblemCodes.InvalidToken, null, "Wrong username or password.")]
    [InlineData(403, ProblemCodes.PermissionDenied, null, "This account is disabled.")]
    [InlineData(429, ProblemCodes.RateLimited, 900, "Too many attempts. Try again in 15 minutes.")]
    [InlineData(429, ProblemCodes.RateLimited, 30, "Too many attempts. Try again in a minute.")]
    [InlineData(503, ProblemCodes.DependencyUnavailable, 1, "The server is busy. Try again in a moment.")]
    [InlineData(500, "internal", null, "Sign-in failed (error 500).")]
    public async Task SignIn_ExplainsServerAnswers(int status, string code, int? retryAfterSeconds, string expected)
    {
        var (viewModel, _) = await LoadedAsync();
        viewModel.Username = "alex";
        viewModel.Password = "pw";
        _session.FailWith = new SiloApiException(status, new ApiProblem(ApiProblem.TypeOrigin + code, null, status, null, null, null),
            retryAfterSeconds is { } s ? TimeSpan.FromSeconds(s) : null);

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.Equal(expected, viewModel.Status);
        Assert.Equal("", viewModel.Password);
    }

    [Fact]
    public async Task SignIn_DirectoryRefusal_ShowsTheServersText()
    {
        var (viewModel, _) = await LoadedAsync();
        viewModel.Username = "alex";
        viewModel.Password = "pw";
        _session.FailWith = new SiloApiException(403,
            new ApiProblem(ApiProblem.TypeOrigin + "password_expired", null, 403, "Your directory password has expired.", null, null));

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.Equal("Your directory password has expired.", viewModel.Status);
    }

    [Fact]
    public async Task SignIn_LocalLoginDisabled_HidesThePasswordForm()
    {
        var (viewModel, _) = await LoadedAsync();
        viewModel.Username = "alex";
        viewModel.Password = "pw";
        _session.FailWith = new SiloApiException(403, new ApiProblem(ApiProblem.TypeOrigin + ProblemCodes.LocalLoginDisabled, null, 403, null, null, null));

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsPasswordSignInAvailable);
        Assert.Contains("code", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignIn_Unreachable_SaysSoWithoutLeakingThePassword()
    {
        var (viewModel, _) = await LoadedAsync();
        viewModel.Username = "alex";
        viewModel.Password = "hunter2";
        _session.FailWith = new HttpRequestException("No such host is known.");

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.Equal("Can't reach Living room. Check the connection and try again.", viewModel.Status);
        Assert.DoesNotContain("hunter2", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnencryptedServer_NeedsConsentBeforeCredentialsAreSent()
    {
        var server = await _registry.AddOrUpdateAsync(new Uri("http://nas.local:8090"), "NAS", null, TestContext.Current.CancellationToken);
        var viewModel = Create();
        await viewModel.LoadAsync(server.Id, TestContext.Current.CancellationToken);
        viewModel.Username = "alex";
        viewModel.Password = "pw";

        Assert.True(viewModel.IsUnencrypted);
        Assert.False(viewModel.CanUseCredentials);
        Assert.False(viewModel.SignInCommand.CanExecute(null));

        viewModel.AcceptUnencryptedCommand.Execute(null);

        Assert.True(viewModel.CanUseCredentials);
        Assert.True(viewModel.SignInCommand.CanExecute(null));
    }

    [Fact]
    public async Task EncryptedServer_NeedsNoConsent()
    {
        var (viewModel, _) = await LoadedAsync();

        Assert.False(viewModel.IsUnencrypted);
        Assert.True(viewModel.CanUseCredentials);
    }

    [Fact]
    public async Task SignIn_UnusableAnswer_PointsAtTheAddress()
    {
        var (viewModel, _) = await LoadedAsync();
        viewModel.Username = "alex";
        viewModel.Password = "pw";
        _session.FailWith = new HttpRequestException(HttpRequestError.InvalidResponse, "not Silo");

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.Equal("Living room answered in a way Granary does not understand. Check the server address.", viewModel.Status);
    }

    [Fact]
    public void UseAnotherServer_OpensTheServerList()
    {
        Create().UseAnotherServerCommand.Execute(null);

        Assert.Equal(AppRoute.Servers, Assert.Single(_navigation.Navigations).Route);
    }
}
