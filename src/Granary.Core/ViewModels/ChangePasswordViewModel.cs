using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api;
using Granary.Core.Api.Accounts;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// Replaces the account password, typically a temporary one that blocks every other call
/// (docs/api-contract.md, 4.1), in the words of the web's ChoosePassword page and, for the server's limits, its account
/// settings' password form. The limits come from the server's password capability; after the change
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

    /// <summary>The first box's label: "Temporary password" while the account has one, as the web's, else "Current password".</summary>
    public string CurrentPasswordLabel => IsTemporary
        ? AppText.Get("web.auth.choosePassword.temporaryPassword")
        : AppText.Get("web.settings.account.password.current");

    private bool IsTemporary => session.Account?.PasswordChangeRequired == true;

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
        Explanation = IsTemporary
            ? AppText.Get("web.auth.choosePassword.description", ("username", session.Account?.Username ?? ""))
            : AppText.Get("granary.changePassword.explanation");
        OnPropertyChanged(nameof(CurrentPasswordLabel));
        IsReady = false;
        HasLoadError = false;
        SetStatus("", isError: false);
        if (session.ActiveServer is not { } server)
        {
            SetStatus(AppText.Get("granary.changePassword.noServer"), isError: true);
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
            SetStatus(DescribeLoadFailure(ex), isError: true);
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
        SetStatus(AppText.Get("web.ui.saving"), isError: false);
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
            SetStatus(AppText.Get("granary.changePassword.signOutFailed"), isError: true);
        }
    }

    private void ApplyCapability(AccountPasswordCapability capability)
    {
        _capability = capability;
        if (capability.State != AvailableState)
        {
            SetStatus(AppText.Get("granary.changePassword.unavailable"), isError: true);
            return;
        }
        Requirements = AppText.Get("web.settings.account.password.requirements", ("minimum", capability.MinimumPasswordLength),
            ("minimum_text", AppText.Number(capability.MinimumPasswordLength)), ("maximum", capability.MaximumPasswordBytes));
        if (!capability.Allowed)
        {
            SetStatus(AppText.Get("web.settings.account.password.error.unavailable"), isError: true);
            return;
        }
        IsReady = true;
    }

    /// <summary>The first local reason the server would refuse the change, or null.</summary>
    private string? Validate()
    {
        if (_capability is not { } capability)
        {
            return AppText.Get("granary.changePassword.rulesNotLoaded");
        }
        if (CurrentPassword.Length == 0)
        {
            return AppText.Get("granary.changePassword.enterCurrent");
        }
        // The server counts Unicode characters (code points), not UTF-16 units.
        if (NewPassword.EnumerateRunes().Count() < capability.MinimumPasswordLength)
        {
            return AppText.Get("web.settings.account.password.error.tooShort", ("minimum", capability.MinimumPasswordLength),
                ("minimum_text", AppText.Number(capability.MinimumPasswordLength)));
        }
        if (Encoding.UTF8.GetByteCount(NewPassword) > capability.MaximumPasswordBytes)
        {
            return AppText.Get("web.settings.account.password.error.tooLong", ("maximum", capability.MaximumPasswordBytes));
        }
        if (!string.Equals(NewPassword, ConfirmPassword, StringComparison.Ordinal))
        {
            return AppText.Get("web.settings.account.password.error.mismatch");
        }
        if (string.Equals(NewPassword, CurrentPassword, StringComparison.Ordinal))
        {
            return AppText.Get("granary.changePassword.sameAsCurrent");
        }
        return null;
    }

    private async Task<bool> TryChangeAsync(ServerEntry server, string current, string replacement)
    {
        try
        {
            await account.ChangePasswordAsync(server.BaseUri, current, replacement);
            logger.LogInformation("Account password changed");
            SetStatus(AppText.Get("web.auth.passwordRules.passwordChanged"), isError: false);
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
            SetStatus(AppText.Get("web.auth.choosePassword.settleFailed"), isError: true);
        }
    }

    private void SetStatus(string text, bool isError)
    {
        Status = text;
        HasError = isError;
    }

    // "Could not read the password rules." and why, as one sentence each.
    private static string DescribeLoadFailure(Exception ex) => ex switch
    {
        SiloApiException { Status: 429, RetryAfter: { } wait } => AppText.Get("granary.changePassword.rulesTooManyRequests",
            ("count", ApiFailureText.WholeMinutes(wait))),
        SiloApiException { Status: 429 } => AppText.Get("granary.changePassword.rulesTooManyRequestsLater"),
        SiloApiException { Status: 503 } => AppText.Get("granary.changePassword.rulesBusy"),
        SiloApiException api => AppText.Get("granary.changePassword.rulesFailed", ("status", api.Status)),
        // Not a Silo server's answer, as ApiFailureText.ForTransport tells it from a server that could not be reached.
        System.Text.Json.JsonException or HttpRequestException { HttpRequestError: HttpRequestError.InvalidResponse }
            => AppText.Get("granary.changePassword.rulesUnexpected"),
        _ => AppText.Get("granary.changePassword.rulesUnreachable"),
    };

    private static string DescribeChangeFailure(Exception ex) => ex switch
    {
        // For example the temporary password again, or a password outside the limits.
        SiloApiException { Status: 422 } api when HasErrorAt(api, "body.new_password") => AppText.Get("granary.changePassword.chooseDifferent"),
        SiloApiException { Status: 422 } api when HasErrorAt(api, "body.current_password") => AppText.Get("granary.changePassword.currentWrong"),
        SiloApiException { Status: 422 } => AppText.Get("granary.changePassword.notAccepted"),
        SiloApiException { Status: 429, RetryAfter: { } wait } => AppText.Get("granary.changePassword.tooManyAttempts",
            ("count", ApiFailureText.WholeMinutes(wait))),
        SiloApiException { Status: 429 } => AppText.Get("granary.changePassword.tooManyAttemptsLater"),
        SiloApiException { Status: 403 } => AppText.Get("web.settings.account.password.error.unavailable"),
        SiloApiException { Status: 409 } => AppText.Get("granary.changePassword.noPassword"),
        SiloApiException { Status: 503 } => ApiFailureText.Busy,
        SiloApiException api => AppText.Get("granary.changePassword.failed", ("status", api.Status)),
        _ => ApiFailureText.ForTransport(ex),
    };

    private static bool HasErrorAt(SiloApiException ex, string location) =>
        ex.Problem.Errors?.Any(e => string.Equals(e.Location, location, StringComparison.Ordinal)) == true;
}
