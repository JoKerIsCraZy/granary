using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Auth;
using Granary.Core.Auth;
using Granary.Core.Servers;
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
    [NotifyPropertyChangedFor(nameof(IsUnencrypted), nameof(CanUseCredentials))]
    public partial ServerEntry? Server { get; private set; }

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
            Status = "This server is no longer saved. Add it again.";
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
            Status = $"{Server.Name} answered in a way Granary does not understand. Check the server address.";
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Password sign-in to server {ServerEntryId} could not reach the server", Server!.Id);
            Status = $"Can't reach {Server.Name}. Check the connection and try again.";
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
        { Status: 401 } => "Wrong username or password.",
        { Code: ProblemCodes.LocalLoginDisabled } => "Password sign-in is turned off on this server. Sign in with the code instead.",
        { Code: ProblemCodes.PermissionDenied } => "This account is disabled.",
        { Status: 429 } => $"Too many attempts. Try again {WaitText(error.RetryAfter)}.",
        { Status: 503 } => "The server is busy. Try again in a moment.",
        // Directory (LDAP) outcomes come with a text meant for people.
        { Status: 403 or 409, Problem.Detail: { Length: > 0 } detail } => detail,
        _ => $"Sign-in failed (error {error.Status}).",
    };

    internal static string WaitText(TimeSpan? retryAfter) => retryAfter switch
    {
        null => "later",
        { TotalSeconds: <= 60 } => "in a minute",
        { } wait => $"in {Math.Ceiling(wait.TotalMinutes):0} minutes",
    };
}
