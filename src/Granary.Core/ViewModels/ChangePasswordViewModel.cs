using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api;
using Granary.Core.Api.Accounts;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// Replaces the account password, typically a temporary one that blocks every other call
/// (docs/api-contract.md, 4.1). The limits come from the server's password capability; after the change
/// the session refreshes its tokens to lift the restriction. Passwords are never logged. The commands take
/// no cancellation token: a cancellable toolkit command would cancel its running call when executed again.
/// </summary>
public sealed partial class ChangePasswordViewModel(AccountApi account, ISessionManager session, ILogger<ChangePasswordViewModel> logger)
    : ObservableObject
{
    private const string AvailableState = "available";

    private AccountPasswordCapability? _capability;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string CurrentPassword { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string NewPassword { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string ConfirmPassword { get; set; } = "";

    [ObservableProperty]
    public partial string Explanation { get; private set; } = "";

    /// <summary>The server's limits for the new password, for example "at least 8 characters".</summary>
    [ObservableProperty]
    public partial string Requirements { get; private set; } = "";

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasError { get; private set; }

    /// <summary>The capability could not be read; the page offers <see cref="LoadCommand"/> again.</summary>
    [ObservableProperty]
    public partial bool HasLoadError { get; private set; }

    /// <summary>The capability is loaded and allows a change.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial bool IsReady { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial bool IsBusy { get; private set; }

    [RelayCommand]
    private async Task LoadAsync()
    {
        Explanation = session.Account?.PasswordChangeRequired == true
            ? "Your account has a temporary password. Choose a new password to continue."
            : "Choose a new password for your account.";
        IsReady = false;
        HasLoadError = false;
        SetStatus("", isError: false);
        if (session.ActiveServer is not { } server)
        {
            SetStatus("No server is selected.", isError: true);
            return;
        }

        try
        {
            ApplyCapability(await account.GetPasswordCapabilityAsync(server.BaseUri));
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            logger.LogWarning("Reading the password capability failed: {Failure}", ApiFailureText.Describe(ex));
            HasLoadError = true;
            SetStatus("Could not read the password rules. " + DescribeLoadFailure(ex), isError: true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        if (!IsReady || IsBusy || session.ActiveServer is not { } server)
        {
            return;
        }
        if (Validate() is { } problem)
        {
            SetStatus(problem, isError: true);
            return;
        }

        string current = CurrentPassword;
        string replacement = NewPassword;
        CurrentPassword = "";
        NewPassword = "";
        ConfirmPassword = "";
        IsBusy = true;
        SetStatus("Changing the password…", isError: false);
        try
        {
            if (await TryChangeAsync(server, current, replacement))
            {
                await RenewSessionAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSubmit() =>
        IsReady && !IsBusy && CurrentPassword.Length > 0 && NewPassword.Length > 0 && ConfirmPassword.Length > 0;

    /// <summary>The way out when the password cannot be changed now; the shell routes on the session change.</summary>
    [RelayCommand]
    private async Task SignOutAsync()
    {
        try
        {
            await session.SignOutAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Signing out failed: {Failure}", ApiFailureText.Describe(ex));
            SetStatus("Could not sign out. Try again.", isError: true);
        }
    }

    private void ApplyCapability(AccountPasswordCapability capability)
    {
        _capability = capability;
        if (capability.State != AvailableState)
        {
            SetStatus("This server does not offer password changes. Ask the server administrator.", isError: true);
            return;
        }
        Requirements = $"Use at least {capability.MinimumPasswordLength} characters and at most "
            + $"{capability.MaximumPasswordBytes} bytes (accented letters and symbols take two or more bytes each).";
        if (!capability.Allowed)
        {
            SetStatus("The password of this account cannot be changed from this session.", isError: true);
            return;
        }
        IsReady = true;
    }

    /// <summary>The first local reason the server would refuse the change, or null.</summary>
    private string? Validate()
    {
        if (_capability is not { } capability)
        {
            return "The password rules are not loaded yet.";
        }
        if (CurrentPassword.Length == 0)
        {
            return "Enter your current password.";
        }
        // The server counts Unicode characters (code points), not UTF-16 units.
        if (NewPassword.EnumerateRunes().Count() < capability.MinimumPasswordLength)
        {
            return $"The new password needs at least {capability.MinimumPasswordLength} characters.";
        }
        if (Encoding.UTF8.GetByteCount(NewPassword) > capability.MaximumPasswordBytes)
        {
            return $"The new password is too long: at most {capability.MaximumPasswordBytes} bytes.";
        }
        if (!string.Equals(NewPassword, ConfirmPassword, StringComparison.Ordinal))
        {
            return "The new passwords do not match.";
        }
        if (string.Equals(NewPassword, CurrentPassword, StringComparison.Ordinal))
        {
            return "Choose a password different from the current one.";
        }
        return null;
    }

    private async Task<bool> TryChangeAsync(ServerEntry server, string current, string replacement)
    {
        try
        {
            await account.ChangePasswordAsync(server.BaseUri, current, replacement);
            logger.LogInformation("Account password changed");
            SetStatus("Your password has been changed.", isError: false);
            return true;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            logger.LogWarning("Changing the password failed: {Failure}", ApiFailureText.Describe(ex));
            SetStatus(DescribeChangeFailure(ex), isError: true);
            return false;
        }
    }

    // New tokens drop the temporary-password restriction; the shell routes on the session change.
    private async Task RenewSessionAsync()
    {
        try
        {
            await session.RefreshAccountAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Renewing the session after the password change failed: {Failure}", ApiFailureText.Describe(ex));
            SetStatus("Your password has been changed, but the session could not be renewed. Sign in again with the new password.",
                isError: true);
        }
    }

    private void SetStatus(string text, bool isError)
    {
        Status = text;
        HasError = isError;
    }

    private static string DescribeLoadFailure(Exception ex) => ex switch
    {
        SiloApiException { Status: 429 } api => ApiFailureText.TryAgainIn(api.RetryAfter),
        SiloApiException { Status: 503 } => ApiFailureText.Busy,
        SiloApiException api => $"The server answered with error {api.Status}.",
        _ => ApiFailureText.ForTransport(ex),
    };

    private static string DescribeChangeFailure(Exception ex) => ex switch
    {
        // For example the temporary password again, or a password outside the limits.
        SiloApiException { Status: 422 } api when HasErrorAt(api, "body.new_password") => "Choose a different password.",
        SiloApiException { Status: 422 } api when HasErrorAt(api, "body.current_password") => "The current password is not right.",
        SiloApiException { Status: 422 } => "The server did not accept the new password.",
        SiloApiException { Status: 429 } api => "Too many attempts. " + ApiFailureText.TryAgainIn(api.RetryAfter),
        SiloApiException { Status: 403 } => "The password of this account cannot be changed from this session.",
        SiloApiException { Status: 409 } => "This account does not sign in with a password on this server.",
        SiloApiException { Status: 503 } => ApiFailureText.Busy,
        SiloApiException api => $"The password could not be changed (error {api.Status}).",
        _ => ApiFailureText.ForTransport(ex),
    };

    private static bool HasErrorAt(SiloApiException ex, string location) =>
        ex.Problem.Errors?.Any(e => string.Equals(e.Location, location, StringComparison.Ordinal)) == true;
}
