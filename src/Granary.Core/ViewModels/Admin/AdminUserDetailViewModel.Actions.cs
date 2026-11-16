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
        if (_session.ActiveServer is not { } server || Account is not { } account || _acting || !await _confirmation.AskAsync(
            AppText.Get("web.admin.users.page.transferTitle", ("username", account.Username)),
            AppText.Get("web.admin.users.page.transferDescription", ("username", account.Username)),
            AppText.Get("web.admin.users.page.transferConfirm")))
        {
            return;
        }
        _acting = true;
        try
        {
            await _users.TransferOwnershipAsync(server.BaseUri, account.Id);
            // The viewer stays an admin, no longer the Owner.
            ViewerIsOwner = false;
            await ReadAsync(keepDrafts: false);
            Say(AppText.Get("web.admin.users.page.ownerTransferred", ("username", account.Username)), isError: false);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
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
        if (_session.ActiveServer is not { } server || Account is not { } account || _acting || !Capabilities.Available)
        {
            return;
        }
        _acting = true;
        Say("", isError: false);
        try
        {
            var fresh = await _users.GetAsync(server.BaseUri, account.Id);
            await _users.UpdateAsync(server.BaseUri, account.Id, fresh.Version, new AdminUserChange { Enabled = enabled });
            await ReadAsync(keepDrafts: true);
            Say(AppText.Get(enabled ? "web.admin.users.page.enabled" : "web.admin.users.page.disabled", ("username", account.Username)), isError: false);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
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
