using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api;
using Granary.Core.Api.Admin;
using Granary.Core.Auth;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// The web's delete dialog (<c>components/AdminUserDeleteDialog.tsx</c>): the account is deleted only once its name is typed,
/// at the version read when the dialog opened. Where the caller offers it, Disable instead keeps the account's history. A
/// change by another administrator meanwhile (412) blocks both until the account is read again.
/// </summary>
public sealed partial class AdminUserDeleteViewModel(AdminUsersApi users, ILogger<AdminUserDeleteViewModel> logger)
    : ObservableObject
{
    private string _version = "";
    // The account and profile this was opened as: everything it sends goes only as them.
    private AdminActor? _actor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(CanDelete), nameof(OffersDisable))]
    public partial AdminUser? Account { get; private set; }

    /// <summary>What the administrator typed; it must be the account's name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    public partial string Typed { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete), nameof(CanDisable))]
    public partial bool IsBusy { get; private set; }

    /// <summary>Another administrator changed the account: reload before anything else.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete), nameof(CanDisable))]
    public partial bool HasConflict { get; private set; }

    [ObservableProperty]
    public partial string Error { get; private set; } = "";

    /// <summary>The caller offers Disable instead (the account's page does).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffersDisable))]
    public partial bool DisableOffered { get; set; }

    public string Title => AppText.Get("web.admin.users.deleteDialog.title", ("username", Account?.Username ?? ""));

    public static string Description => AppText.Get("web.admin.users.deleteDialog.description");

    public bool OffersDisable => DisableOffered && Account is { Enabled: true };

    public bool CanDelete => Account is not null && !IsBusy && !HasConflict && Typed == Account.Username;

    public bool CanDisable => !IsBusy && !HasConflict;

    /// <summary>Reads the account fresh, so the delete carries its current version. False when it could not be read.</summary>
    public async Task<bool> StartAsync(AdminActor? actor, string id, CancellationToken cancellationToken = default)
    {
        Account = null;
        Typed = Error = "";
        HasConflict = false;
        _actor = actor;
        return await ReadAsync(id, "web.admin.users.list.userLoadFailed", cancellationToken);
    }

    /// <summary>Deletes the account; true when it is gone.</summary>
    public Task<bool> DeleteAsync(CancellationToken cancellationToken = default) =>
        RunAsync((actor, account) => users.DeleteAsync(actor, account.Id, _version, cancellationToken),
            "web.admin.users.deleteDialog.deleteFailed", requireName: true);

    /// <summary>Disables the account instead; true when it is.</summary>
    public Task<bool> DisableAsync(CancellationToken cancellationToken = default) =>
        RunAsync((actor, account) => users.UpdateAsync(actor, account.Id, _version, new AdminUserChange { Enabled = false }, cancellationToken),
            "web.admin.users.deleteDialog.disableFailed", requireName: false);

    /// <summary>After a conflict: reads the account again, so the next try carries its new version.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (Account is not { } account || IsBusy)
        {
            return;
        }
        if (await ReadAsync(account.Id, "web.admin.users.deleteDialog.reloadFailed", cancellationToken))
        {
            HasConflict = false;
        }
    }

    private async Task<bool> ReadAsync(string id, string failureKey, CancellationToken cancellationToken)
    {
        if (_actor is not { } actor)
        {
            return false;
        }
        IsBusy = true;
        try
        {
            var read = await users.GetAsync(actor, id, cancellationToken);
            Account = read.Value;
            _version = read.Version;
            Error = "";
            return true;
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex) && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read the account to delete");
            Error = AdminFailures.Describe(ex, AppText.Get(failureKey));
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> RunAsync(Func<AdminActor, AdminUser, Task> action, string fallbackKey, bool requireName)
    {
        if (_actor is not { } actor || Account is not { } account || IsBusy || HasConflict
            || (requireName && Typed != account.Username))
        {
            return false;
        }
        IsBusy = true;
        Error = "";
        try
        {
            await action(actor, account);
            return true;
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            logger.LogWarning(ex, "Could not delete or disable an account");
            HasConflict = AdminFailures.IsChanged(ex);
            Error = HasConflict ? AppText.Get("web.admin.users.deleteDialog.conflict") : AdminFailures.Describe(ex, AppText.Get(fallbackKey));
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
