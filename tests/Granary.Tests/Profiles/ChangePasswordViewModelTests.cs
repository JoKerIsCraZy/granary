using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.Accounts;
using Granary.Core.Api.Auth;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.Profiles;

public sealed class ChangePasswordViewModelTests
{
    private const string CapabilityPath = "api/v2/account/password/capability";
    private const string PasswordPath = "api/v2/account/password";
    private const string Temporary = "temp-pass-1";
    private const string Replacement = "margin fossil quench";

    private const string CapabilityJson = """
        {"revision": "r1", "state": "available", "allowed": true, "requires_current_password": true,
         "minimum_password_length": 8, "maximum_password_bytes": 72}
        """;

    private sealed class Harness
    {
        public Harness(string capabilityJson = CapabilityJson)
        {
            ViewModel = new ChangePasswordViewModel(new AccountApi(Silo.Http), Session, Logger);
            Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, capabilityJson));
        }

        public ScriptedSilo Silo { get; } = new();

        public FakeSessionManager Session { get; } = new()
        {
            ActiveServer = ScriptedSilo.Server,
            State = SessionState.PasswordChangeRequired,
            Account = new Account("1", "alex", "", "user", [], true, PasswordChangeRequired: true, null),
        };

        public RecordingLogger<ChangePasswordViewModel> Logger { get; } = new();

        public ChangePasswordViewModel ViewModel { get; }

        public async Task LoadAndFillAsync(string current = Temporary, string replacement = Replacement, string? confirmation = null)
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
            ViewModel.CurrentPassword = current;
            ViewModel.NewPassword = replacement;
            ViewModel.ConfirmPassword = confirmation ?? replacement;
        }
    }

    [Fact]
    public async Task Load_ShowsTheServersLimitsAndExplainsTheTemporaryPassword()
    {
        var harness = new Harness();

        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsReady);
        Assert.Contains("at least 8 characters", harness.ViewModel.Requirements, StringComparison.Ordinal);
        Assert.Contains("at most 72 bytes", harness.ViewModel.Requirements, StringComparison.Ordinal);
        Assert.Contains("temporary password", harness.ViewModel.Explanation, StringComparison.Ordinal);
        Assert.Equal("", harness.ViewModel.Status);
        var request = Assert.Single(harness.Silo.Sent);
        Assert.Equal("https://nas.example/silo/api/v2/account/password/capability", request.Uri.AbsoluteUri);
        Assert.Equal(SiloAuth.Account, request.Auth);
    }

    [Fact]
    public async Task Submit_IsEnabledOnlyWithTheCapabilityAndAllThreeFields()
    {
        var harness = new Harness();
        Assert.False(harness.ViewModel.SubmitCommand.CanExecute(null));

        await harness.ViewModel.LoadCommand.ExecuteAsync(null);
        harness.ViewModel.CurrentPassword = Temporary;
        harness.ViewModel.NewPassword = Replacement;
        Assert.False(harness.ViewModel.SubmitCommand.CanExecute(null));

        harness.ViewModel.ConfirmPassword = Replacement;
        Assert.True(harness.ViewModel.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public async Task Load_WhenTheSessionMayNotChangeThePassword_DisablesSubmit()
    {
        var harness = new Harness(CapabilityJson.Replace("\"allowed\": true", "\"allowed\": false", StringComparison.Ordinal));

        await harness.LoadAndFillAsync();

        Assert.False(harness.ViewModel.IsReady);
        Assert.True(harness.ViewModel.HasError);
        Assert.False(harness.ViewModel.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public async Task Load_Failure_OffersToTryAgain()
    {
        var harness = new Harness();
        harness.Silo.On(HttpMethod.Get, CapabilityPath,
            ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable),
            ScriptedSilo.Json(HttpStatusCode.OK, CapabilityJson));

        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.HasLoadError);
        Assert.False(harness.ViewModel.IsReady);
        Assert.Equal("Could not read the password rules. The server is busy, try again.", harness.ViewModel.Status);

        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.HasLoadError);
        Assert.True(harness.ViewModel.IsReady);
    }

    public static TheoryData<string, string, string, string> InvalidInput => new()
    {
        { Temporary, "short", "short", "The new password needs at least 8 characters." },
        // Four code points but eight UTF-16 units: the server counts code points.
        { Temporary, "😀😀😀😀", "😀😀😀😀", "The new password needs at least 8 characters." },
        { Temporary, new string('€', 25), new string('€', 25), "The new password is too long: at most 72 bytes." },
        { Temporary, Replacement, Replacement + "!", "The new passwords do not match." },
        { Replacement, Replacement, Replacement, "Choose a password different from the current one." },
    };

    [Theory]
    [MemberData(nameof(InvalidInput))]
    public async Task Submit_InvalidInput_ExplainsWhyWithoutCallingTheServer(string current, string replacement, string confirmation, string expected)
    {
        var harness = new Harness();
        await harness.LoadAndFillAsync(current, replacement, confirmation);

        await harness.ViewModel.SubmitCommand.ExecuteAsync(null);

        Assert.Empty(harness.Silo.SentTo(HttpMethod.Post, PasswordPath));
        Assert.Equal(expected, harness.ViewModel.Status);
        Assert.True(harness.ViewModel.HasError);
        Assert.Equal(replacement, harness.ViewModel.NewPassword);
    }

    [Fact]
    public async Task Submit_Success_ChangesThePasswordAndRenewsTheSession()
    {
        var harness = new Harness();
        harness.Silo.On(HttpMethod.Post, PasswordPath, ScriptedSilo.NoContent());
        await harness.LoadAndFillAsync();

        await harness.ViewModel.SubmitCommand.ExecuteAsync(null);

        var change = Assert.Single(harness.Silo.SentTo(HttpMethod.Post, PasswordPath));
        Assert.Equal("""{"current_password":"temp-pass-1","new_password":"margin fossil quench"}""", change.Body);
        Assert.Equal(SiloAuth.Account, change.Auth);
        Assert.Equal(["refresh-account"], harness.Session.Calls);
        Assert.Equal("Your password has been changed.", harness.ViewModel.Status);
        Assert.False(harness.ViewModel.HasError);
        Assert.False(harness.ViewModel.IsBusy);
        AssertFieldsCleared(harness.ViewModel);
        AssertNoPasswordLogged(harness);
    }

    [Theory]
    [InlineData("body.new_password", "Choose a different password.")]
    [InlineData("body.current_password", "The current password is not right.")]
    [InlineData("body.other", "The server did not accept the new password.")]
    public async Task Submit_RejectedMember_NamesTheProblem(string location, string expected)
    {
        var harness = new Harness();
        string errors = $$"""[{"location": "{{location}}", "code": "invalid", "detail": "Choose a password different from the temporary one."}]""";
        harness.Silo.On(HttpMethod.Post, PasswordPath, ScriptedSilo.Problem(422, ProblemCodes.ValidationFailed, errorsJson: errors));
        await harness.LoadAndFillAsync();

        await harness.ViewModel.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(expected, harness.ViewModel.Status);
        Assert.True(harness.ViewModel.HasError);
        Assert.Empty(harness.Session.Calls);
        AssertFieldsCleared(harness.ViewModel);
        AssertNoPasswordLogged(harness);
    }

    [Fact]
    public async Task Submit_RateLimited_AsksToTryLater()
    {
        var harness = new Harness();
        harness.Silo.On(HttpMethod.Post, PasswordPath, ScriptedSilo.Problem(429, ProblemCodes.RateLimited, TimeSpan.FromSeconds(120)));
        await harness.LoadAndFillAsync();

        await harness.ViewModel.SubmitCommand.ExecuteAsync(null);

        Assert.Single(harness.Silo.SentTo(HttpMethod.Post, PasswordPath));
        Assert.Equal("Too many attempts. Try again in 2 minutes.", harness.ViewModel.Status);
        Assert.Empty(harness.Session.Calls);
    }

    [Fact]
    public async Task Submit_NetworkError_ShowsAFriendlyMessage()
    {
        var harness = new Harness();
        harness.Silo.On(HttpMethod.Post, PasswordPath, ScriptedSilo.NetworkError());
        await harness.LoadAndFillAsync();

        await harness.ViewModel.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("Could not reach the server. Check the connection and try again.", harness.ViewModel.Status);
        Assert.True(harness.ViewModel.HasError);
        AssertNoPasswordLogged(harness);
    }

    [Fact]
    public async Task Submit_SessionRenewalFails_AsksToSignInAgain()
    {
        var harness = new Harness();
        harness.Silo.On(HttpMethod.Post, PasswordPath, ScriptedSilo.NoContent());
        await harness.LoadAndFillAsync();
        harness.Session.FailWith = new HttpRequestException("refresh failed");

        await harness.ViewModel.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(["refresh-account"], harness.Session.Calls);
        Assert.Contains("Sign in again with the new password", harness.ViewModel.Status, StringComparison.Ordinal);
        Assert.True(harness.ViewModel.HasError);
    }

    private static void AssertFieldsCleared(ChangePasswordViewModel viewModel)
    {
        Assert.Equal("", viewModel.CurrentPassword);
        Assert.Equal("", viewModel.NewPassword);
        Assert.Equal("", viewModel.ConfirmPassword);
    }

    private static void AssertNoPasswordLogged(Harness harness)
    {
        Assert.NotEmpty(harness.Logger.Lines);
        Assert.DoesNotContain(harness.Logger.Lines, line =>
            line.Contains(Temporary, StringComparison.Ordinal) || line.Contains(Replacement, StringComparison.Ordinal));
    }
}
