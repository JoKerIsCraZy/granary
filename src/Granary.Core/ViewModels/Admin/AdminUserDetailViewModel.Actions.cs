using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Admin;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// The header's actions, as the web's: Disable (asked first) and Enable, each on the account read fresh so it carries its
/// current version, and Make owner (asked first). Delete and the password are dialogs of their own.
/// </summary>
public sealed partial class AdminUserDetailViewModel
{
    private bool _acting;

    [RelayCommand]
    private async Task DisableAsync()
    {
        if (Account is not { } account || !await _confirmation.AskAsync(
            AppText.Get("web.admin.users.page.disableTitle", ("username", account.Username)),
            AppText.Get("web.admin.users.page.disableDescription"),
            AppText.Get("web.admin.users.page.disableConfirm")))
        {
            return;
        }
        await SetEnabledAsync(false);
    }

    [RelayCommand]
    private Task EnableAsync() => SetEnabledAsync(true);

    [RelayCommand]
    private async Task MakeOwnerAsync()
    {
        if (_actor is not { } actor || Account is null || _acting)
        {
            return;
        }
        _acting = true;
        try
        {
            // Read fresh first: the account may have been renamed, disabled or demoted since the page read it.
            _readFailure = null;
            if (!await ReadAsync(keepDrafts: true) || Account is not { } account)
            {
                // Worded by what went wrong: a switch or a 403 is a refusal, as the transfer's own failure would be.
                Say(_readFailure is { } failure
                    ? AdminFailures.Describe(failure, AppText.Get("web.admin.users.detail.access.card.reloadFailed"))
                    : AppText.Get("web.admin.users.detail.access.card.reloadFailed"), isError: true);
                return;
            }
            if (!AdminAccountRules.CanTransferOwnership(account, ViewerId, ViewerIsOwner))
            {
                Say(AppText.Get("web.admin.users.page.transferFailed"), isError: true);
                return;
            }
            if (!await _confirmation.AskAsync(
                AppText.Get("web.admin.users.page.transferTitle", ("username", account.Username)),
                AppText.Get("web.admin.users.page.transferDescription", ("username", account.Username)),
                AppText.Get("web.admin.users.page.transferConfirm")))
            {
                return;
            }
            await _users.TransferOwnershipAsync(actor, account.Id);
            // The viewer stays an admin, no longer the Owner.
            ViewerIsOwner = false;
            await ReadAsync(keepDrafts: false);
            Say(AppText.Get("web.admin.users.page.ownerTransferred", ("username", account.Username)), isError: false);
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not transfer the server's ownership");
            Say(AdminFailures.Describe(ex, AppText.Get("web.admin.users.page.transferFailed")), isError: true);
        }
        finally
        {
            _acting = false;
        }
    }

    /// <summary>After the delete dialog: gone, or disabled instead.</summary>
    public async Task DeletedOrDisabledAsync(bool deleted)
    {
        if (deleted)
        {
            Close();
            return;
        }
        string username = Account?.Username ?? "";
        await ReadAsync(keepDrafts: true);
        Say(AppText.Get("web.admin.users.page.disabled", ("username", username)), isError: false);
    }

    // Read fresh first, so the change carries the account's current version; 412 says another administrator changed it.
    private async Task SetEnabledAsync(bool enabled)
    {
        if (_actor is not { } actor || Account is not { } account || _acting || !Capabilities.Available)
        {
            return;
        }
        _acting = true;
        Say("", isError: false);
        try
        {
            var fresh = await _users.GetAsync(actor, account.Id);
            await _users.UpdateAsync(actor, account.Id, fresh.Version, new AdminUserChange { Enabled = enabled });
            await ReadAsync(keepDrafts: true);
            Say(AppText.Get(enabled ? "web.admin.users.page.enabled" : "web.admin.users.page.disabled", ("username", account.Username)), isError: false);
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not enable or disable an account");
            Say(AdminFailures.IsChanged(ex) ? AppText.Get("web.admin.users.page.accountChanged")
                : AdminFailures.Describe(ex, AppText.Get("web.admin.users.page.changeFailed")), isError: true);
        }
        finally
        {
            _acting = false;
        }
    }
}
