using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Auth;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// Password sign-in for one saved server (docs/api-contract.md, 4.1). Device sign-in runs beside it in
/// its own view model. On success the session changes and the shell moves on.
/// </summary>
public sealed partial class SignInViewModel(
    ISessionManager session,
    IServerRegistry registry,
    AuthApi auth,
    INavigationService navigation,
    ILogger<SignInViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    [NotifyPropertyChangedFor(nameof(IsUnencrypted), nameof(CanUseCredentials), nameof(ServerName))]
    public partial ServerEntry? Server { get; private set; }

    /// <summary>
    /// The server's name, empty while none is known: what the page's title is bound to. x:Bind does not call a function for
    /// an argument that is null, so bound to <see cref="Server"/> the title stayed empty without one.
    /// </summary>
    public string ServerName => Server?.Name ?? "";

    /// <summary>
    /// A plain http:// server: passwords and tokens would travel readable by anyone on the network, so
    /// neither form works until the user agrees (docs/api-contract.md, 3.2).
    /// </summary>
    public bool IsUnencrypted => Server?.BaseUri.Scheme == Uri.UriSchemeHttp;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    [NotifyPropertyChangedFor(nameof(CanUseCredentials))]
    public partial bool IsUnencryptedAccepted { get; private set; }

    /// <summary>True when sign-in may send credentials: the connection is encrypted, or the user accepted that it is not.</summary>
    public bool CanUseCredentials => Server is not null && (!IsUnencrypted || IsUnencryptedAccepted);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Username { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Password { get; set; } = "";

    /// <summary>False when the server only allows external sign-in; device sign-in still works then.</summary>
    [ObservableProperty]
    public partial bool IsPasswordSignInAvailable { get; private set; } = true;

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    public async Task LoadAsync(string? serverEntryId, CancellationToken cancellationToken = default)
    {
        Server = (serverEntryId is null ? null : registry.Find(serverEntryId)) ?? session.ActiveServer;
        if (Server is null)
        {
            Status = AppText.Get("granary.signIn.serverGone");
            return;
        }
        try
        {
            var providers = await auth.GetProvidersAsync(Server.BaseUri, cancellationToken);
            IsPasswordSignInAvailable = providers.PasswordLogin;
        }
        catch (Exception ex) when (ex is HttpRequestException or SiloApiException)
        {
            // Keep the form: the login answer says what is allowed.
            logger.LogWarning(ex, "Could not read the sign-in options of server {ServerEntryId}", Server.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        Status = "";
        try
        {
            await session.SignInWithPasswordAsync(Server!, Username.Trim(), Password, cancellationToken);
        }
        catch (SiloApiException ex)
        {
            logger.LogInformation("Password sign-in to server {ServerEntryId} failed with {Status} {Code}", Server!.Id, ex.Status, ex.Code);
            Status = Describe(ex);
            if (ex.Code == ProblemCodes.LocalLoginDisabled)
            {
                IsPasswordSignInAvailable = false;
            }
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.InvalidResponse)
        {
            logger.LogWarning(ex, "Password sign-in to server {ServerEntryId} got an answer it cannot use", Server!.Id);
            Status = AppText.Get("granary.signIn.unexpectedAnswer", ("name", Server.Name));
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Password sign-in to server {ServerEntryId} could not reach the server", Server!.Id);
            Status = AppText.Get("granary.signIn.unreachable", ("name", Server.Name));
        }
        finally
        {
            Password = "";
        }
    }

    [RelayCommand]
    private void UseAnotherServer() => navigation.NavigateTo(AppRoute.Servers);

    [RelayCommand]
    private void AcceptUnencrypted()
    {
        logger.LogInformation("The user accepted unencrypted sign-in to server {ServerEntryId}", Server?.Id);
        IsUnencryptedAccepted = true;
    }

    private bool CanSignIn() => CanUseCredentials && !string.IsNullOrWhiteSpace(Username) && Password.Length > 0;

    internal static string Describe(SiloApiException error) => error switch
    {
        // There is no dedicated code for bad credentials (docs/api-contract.md, 4.1).
        { Status: 401 } => AppText.Get("granary.signIn.wrongCredentials"),
        { Code: ProblemCodes.LocalLoginDisabled } => AppText.Get("granary.signIn.passwordDisabled"),
        // As the web's sign-in words permission_denied: before sign-in the only denial is a disabled account.
        { Code: ProblemCodes.PermissionDenied } => AppText.Get("web.auth.oauthFailure.accountDisabled"),
        { Status: 429 } => TooManyAttempts(error.RetryAfter),
        { Status: 503 } => AppText.Get("granary.signIn.busy"),
        // Directory (LDAP) outcomes come with a text meant for people.
        { Status: 403 or 409, Problem.Detail: { Length: > 0 } detail } => detail,
        _ => AppText.Get("granary.signIn.failed", ("status", error.Status)),
    };

    // A whole message for each wait: later, in a minute, or in so many minutes.
    private static string TooManyAttempts(TimeSpan? retryAfter) => retryAfter switch
    {
        null => AppText.Get("granary.signIn.tooManyAttempts.later"),
        { TotalSeconds: <= 60 } => AppText.Get("granary.signIn.tooManyAttempts.inAMinute"),
        { } wait => AppText.Get("granary.signIn.tooManyAttempts.minutes", ("count", ApiFailureText.WholeMinutes(wait))),
    };
}
