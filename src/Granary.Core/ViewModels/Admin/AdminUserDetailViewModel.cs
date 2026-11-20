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
    // The account and profile the page was opened as: everything it sends goes only as them.
    private AdminActor? _actor;
    // Why the last read failed, for an action that needed it.
    private Exception? _readFailure;
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

    /// <summary>The account and profile this was opened as; its dialogs act as them too.</summary>
    public AdminActor? Actor => _actor;

    public bool ViewerIsOwner
    {
        get;
        private set
        {
            if (field != value)
            {
                field = value;
                // What the header offers depends on it, and so does whether the sessions can be managed.
                OnPropertyChanged();
                OnPropertyChanged(nameof(Header));
            }
        }
    }

    /// <summary>The account the page was opened as, not whoever is signed in now.</summary>
    public string? ViewerId => _actor?.AccountId;

    /// <summary>The header's words and the actions it offers, for <see cref="Account"/>.</summary>
    public AdminUserHeader? Header => Account is { } account
        ? AdminUserHeader.For(account, GroupName(account.AccessGroupId), Capabilities, ViewerId, ViewerIsOwner, _time.GetUtcNow())
        : null;

    /// <summary>
    /// Opens an account, with what the list read: the account and profile it acts as, what the server offers, its groups, and
    /// whether the viewer is its Owner.
    /// </summary>
    public async Task OpenAsync(AdminActor? actor, string id, AdminUserCapabilities capabilities, IReadOnlyList<AdminAccessGroup> groups,
        bool viewerIsOwner)
    {
        _id = id;
        _actor = actor;
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

    internal Task<bool> ReadAsync(bool keepDrafts) => ReadAsync(keepDrafts, keepDrafts);

    /// <summary>
    /// Reads the account. A card that keeps its drafts keeps only the fields edited on the account read before, so a change
    /// another administrator made to a field nobody touched here shows, and is not sent back. False when it was not read.
    /// </summary>
    internal async Task<bool> ReadAsync(bool keepSignIn, bool keepAccess, AdminUser? signInBase = null, AdminUser? accessBase = null)
    {
        if (_actor is not { } actor || _id.Length == 0)
        {
            return false;
        }
        _load?.Cancel();
        var load = _load = new CancellationTokenSource();
        try
        {
            var read = await _users.GetAsync(actor, _id, load.Token);
            if (load.Token.IsCancellationRequested)
            {
                return false;
            }
            var before = Account;
            // Edits are measured against the account read before, or for a card just saved against what it sent.
            var signIn = keepSignIn ? SignInDraft(signInBase ?? before) : null;
            var access = keepAccess ? AccessDraft(accessBase ?? before) : null;
            _version = read.Version;
            Account = read.Value;
            ResetSignIn(signIn);
            ResetAccess(access);
            State = LibraryLoadState.Ready;
            return true;
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex) && !load.Token.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not read an account");
            _readFailure = ex;
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
            return false;
        }
        catch (Exception) when (load.Token.IsCancellationRequested)
        {
            // A newer read, or the page left.
            return false;
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
