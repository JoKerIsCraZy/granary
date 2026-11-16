using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Admin;
using Granary.Core.Auth;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// One account in Admin → Users, as the web's <c>pages/AdminUserDetail.tsx</c>: its header (name, role, status, what it is,
/// and the actions the viewer may take), its sign-in and its library access, each changed at the version the account
/// was read at, and its sign-ins. Its profiles, preferences, downloads and history stay on the web.
/// </summary>
public sealed partial class AdminUserDetailViewModel : ObservableObject
{
    private readonly ISessionManager _session;
    private readonly AdminUsersApi _users;
    private readonly AdminApi _admin;
    private readonly IConfirmation _confirmation;
    private readonly TimeProvider _time;
    private readonly ILogger<AdminUserDetailViewModel> _logger;
    private string _id = "";
    private string _version = "";
    private CancellationTokenSource? _load;

    public AdminUserDetailViewModel(ISessionManager session, AdminUsersApi users, AdminApi admin, IConfirmation confirmation, TimeProvider time,
        ILogger<AdminUserDetailViewModel> logger)
    {
        _session = session;
        _users = users;
        _admin = admin;
        _confirmation = confirmation;
        _time = time;
        _logger = logger;
    }

    /// <summary>The account was deleted: the page goes back to the list.</summary>
    public event EventHandler? Closed;

    [ObservableProperty]
    public partial LibraryLoadState State { get; private set; } = LibraryLoadState.Idle;

    /// <summary>The account as last read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    public partial AdminUser? Account { get; private set; }

    /// <summary>What a done action says ("ana is disabled"), or why it failed.</summary>
    [ObservableProperty]
    public partial string Notice { get; private set; } = "";

    [ObservableProperty]
    public partial bool NoticeIsError { get; private set; }

    public AdminUserCapabilities Capabilities { get; private set; } = new();

    public IReadOnlyList<AdminAccessGroup> Groups { get; private set; } = [];

    public bool ViewerIsOwner { get; private set; }

    public string? ViewerId => _session.Account?.Id;

    /// <summary>The header's words and the actions it offers, for <see cref="Account"/>.</summary>
    public AdminUserHeader? Header => Account is { } account
        ? AdminUserHeader.For(account, GroupName(account.AccessGroupId), Capabilities, ViewerId, ViewerIsOwner, _time.GetUtcNow())
        : null;

    /// <summary>Opens an account, with what the list read: what the server offers, its groups, and whether the viewer is its Owner.</summary>
    public async Task OpenAsync(string id, AdminUserCapabilities capabilities, IReadOnlyList<AdminAccessGroup> groups, bool viewerIsOwner)
    {
        _id = id;
        Capabilities = capabilities;
        Groups = groups;
        ViewerIsOwner = viewerIsOwner;
        OnPropertyChanged(nameof(Capabilities));
        Account = null;
        Notice = "";
        State = LibraryLoadState.Loading;
        var libraries = ReadLibrariesAsync();
        await ReadAsync(keepDrafts: false);
        await libraries;
    }

    /// <summary>The page left: reads still running are dropped.</summary>
    public void Deactivate() => _load?.Cancel();

    /// <summary>Reads the account again, as after another administrator changed it.</summary>
    public Task ReloadAsync() => ReadAsync(keepDrafts: true);

    internal async Task ReadAsync(bool keepDrafts)
    {
        if (_session.ActiveServer is not { } server || _id.Length == 0)
        {
            return;
        }
        _load?.Cancel();
        var load = _load = new CancellationTokenSource();
        try
        {
            var read = await _users.GetAsync(server.BaseUri, _id, load.Token);
            if (load.Token.IsCancellationRequested)
            {
                return;
            }
            _version = read.Version;
            Account = read.Value;
            ResetSignIn(keepDrafts);
            ResetAccess(keepDrafts);
            State = LibraryLoadState.Ready;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) && !load.Token.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not read an account");
            // A 404 means it is gone (another administrator deleted it), and outranks a copy read before.
            if (ex is SiloApiException { Status: 404 })
            {
                Account = null;
                State = LibraryLoadState.Unavailable;
            }
            else if (Account is null)
            {
                State = LibraryLoadState.Error;
            }
        }
        catch (Exception) when (load.Token.IsCancellationRequested)
        {
            // A newer read, or the page left.
        }
    }

    /// <summary>Shows what an action did, or why it failed.</summary>
    public void Say(string text, bool isError)
    {
        Notice = text;
        NoticeIsError = isError;
    }

    internal void Close() => Closed?.Invoke(this, EventArgs.Empty);

    private string? GroupName(string? id) => id is null ? null : Groups.FirstOrDefault(group => group.Id == id)?.Name;
}

/// <summary>
/// The web's <c>UserDetailHeader</c>: the account's name, badges and line of facts, why it may be view-only or disabled,
/// and which actions the viewer has.
/// </summary>
public sealed record AdminUserHeader(
    string Username,
    string RoleText,
    bool IsAdmin,
    bool IsEnabled,
    string StatusText,
    bool HasTemporaryPassword,
    string Facts,
    string ViewOnlyNotice,
    bool Manageable,
    bool PolicyManageable,
    bool OwnAccount,
    bool ShowsReset,
    bool ShowsSetPassword,
    bool CanTransfer,
    bool CanDisable,
    bool CanEnable,
    bool CanDelete)
{
    public bool HasMenu => CanTransfer || CanDisable || CanEnable || CanDelete;

    public bool IsViewOnly => ViewOnlyNotice.Length > 0;

    public static AdminUserHeader For(AdminUser user, string? groupName, AdminUserCapabilities capabilities, string? viewerId, bool viewerIsOwner,
        DateTimeOffset now)
    {
        bool manageable = AdminAccountRules.CanManage(user, viewerId, viewerIsOwner);
        bool own = user.Id == viewerId;
        List<string> facts = [];
        if (user.Email.Length > 0)
        {
            facts.Add(user.Email);
        }
        if (!user.IsAdmin && user.AccessGroupId is { } group)
        {
            string name = groupName ?? AppText.Get("web.admin.users.detail.header.groupIdFallback", ("id", group));
            facts.Add(AppText.Get("web.admin.users.detail.header.group", ("name", name)));
        }
        facts.Add(AppText.Get("web.admin.users.detail.header.joined", ("date", DateText.MediumDate(user.CreatedAt.LocalDateTime))));
        facts.Add(user.LastActiveAt is { } seen
            ? AppText.Get("web.admin.users.detail.header.lastActive", ("when", LastSeen(seen, now)))
            : AppText.Get("web.admin.users.detail.header.noActivity"));
        facts.Add(AppText.Get(user.PasswordLogin ? "web.admin.users.detail.header.passwordSignIn" : "web.admin.users.detail.header.externalSignIn"));
        return new AdminUserHeader(
            user.Username,
            AdminText.Role(user.Role, user.IsOwner),
            user.IsAdmin,
            user.Enabled,
            AppText.Get(user.Enabled ? "web.admin.users.detail.header.status.active" : "web.admin.users.detail.header.status.disabled"),
            user.PasswordChangeRequired,
            string.Join(" · ", facts),
            manageable ? "" : AppText.Get(user.IsOwner ? "web.admin.users.detail.header.ownerNotice" : "web.admin.users.detail.header.adminNotice"),
            manageable,
            AdminAccountRules.CanChangeAccess(user, viewerId, viewerIsOwner),
            own,
            ShowsReset: user.PasswordLogin && manageable,
            ShowsSetPassword: !user.PasswordLogin && manageable,
            CanTransfer: capabilities.OwnershipTransfer && AdminAccountRules.CanTransferOwnership(user, viewerId, viewerIsOwner),
            CanDisable: manageable && user.Enabled && !user.IsOwner && !own,
            CanEnable: manageable && !user.Enabled,
            CanDelete: manageable && !user.IsOwner && !own);
    }

    // The web's formatLastSeen: "Just now", "5m ago", up to 30 days; the date after that.
    private static string LastSeen(DateTimeOffset seen, DateTimeOffset now) => now - seen >= TimeSpan.FromDays(30)
        ? DateText.MediumDate(seen.LocalDateTime)
        : AdminText.Ago(seen, now, AppText.Get("web.common.date.justNow"));
}
