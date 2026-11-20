using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Admin;
using Granary.Core.Auth;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// Admin → Users, its Users tab (docs/ui-design.md, Admin), as the web's <c>pages/AdminUsers.tsx</c>: every account, read
/// page by page as the web reads them, then searched, filtered by group, sorted and shown a page at a time here. The
/// list is personal data: names and addresses are shown, never logged.
/// </summary>
public sealed partial class AdminUsersViewModel : ObservableObject
{
    private readonly ISessionManager _session;
    private readonly AdminUsersApi _users;
    private readonly TimeProvider _time;
    // The account and profile this was opened as: everything it sends goes only as them.
    private AdminActor? _actor;
    private readonly ILogger<AdminUsersViewModel> _logger;
    private IReadOnlyList<AdminUser> _accounts = [];
    private IReadOnlyDictionary<string, string> _groupNames = new Dictionary<string, string>();
    private CancellationTokenSource? _load;

    public AdminUsersViewModel(ISessionManager session, AdminUsersApi users, TimeProvider time, ILogger<AdminUsersViewModel> logger)
    {
        _session = session;
        _users = users;
        _time = time;
        _logger = logger;
        GroupFilters = DefaultFilters();
        SelectedGroup = GroupFilters[0];
    }

    /// <summary>Asks the page to open an account (<see cref="AdminAccountRow.Id"/>).</summary>
    public event EventHandler<string>? UserRequested;

    [ObservableProperty]
    public partial LibraryLoadState State { get; private set; } = LibraryLoadState.Idle;

    /// <summary>The server offers account administration; false shows the web's "unavailable" and turns its actions off.</summary>
    [ObservableProperty]
    public partial bool IsAvailable { get; private set; } = true;

    [ObservableProperty]
    public partial bool LoadFailed { get; private set; }

    /// <summary>The groups could not be read: their names and the group filter are missing.</summary>
    [ObservableProperty]
    public partial bool GroupsFailed { get; private set; }

    /// <summary>What the server offers, for the pages and dialogs of the area; empty until read.</summary>
    public AdminUserCapabilities Capabilities { get; private set; } = new();

    /// <summary>The access groups, for the filter, the new account's group and the account's own page.</summary>
    public IReadOnlyList<AdminAccessGroup> Groups { get; private set; } = [];

    /// <summary>The signed-in account's id.</summary>
    public string? ViewerId => _session.Account?.Id;

    /// <summary>The account and profile this was opened as; its dialogs act as them too.</summary>
    public AdminActor? Actor => _actor;

    /// <summary>The viewer is the server's Owner, as the list says.</summary>
    public bool ViewerIsOwner { get; private set; }

    /// <summary>
    /// Opens the page as the account and profile signed in now, and reads it. Its refreshes and dialogs keep acting as them.
    /// </summary>
    public Task OpenAsync()
    {
        var actor = _session.State == SessionState.Ready ? AdminActors.Of(_session) : null;
        if (actor != _actor)
        {
            // Another account or profile: nothing the first one read is shown or offered to its dialogs.
            _load?.Cancel();
            _accounts = [];
            Groups = [];
            Capabilities = new AdminUserCapabilities();
            IsAvailable = false;
            ViewerIsOwner = false;
            LoadFailed = false;
            State = LibraryLoadState.Idle;
            Apply();
        }
        _actor = actor;
        return LoadAsync();
    }

    /// <summary>Reads the accounts, the groups and what the server offers, all at once, as the page was opened.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_actor is not { } actor)
        {
            return;
        }
        _load?.Cancel();
        var load = _load = new CancellationTokenSource();
        var token = load.Token;
        if (State != LibraryLoadState.Ready)
        {
            State = LibraryLoadState.Loading;
        }
        var capabilities = ReadCapabilitiesAsync(actor, token);
        var groups = ReadGroupsAsync(actor, token);
        try
        {
            var accounts = await _users.ListAllAsync(actor, token);
            await Task.WhenAll(capabilities, groups);
            if (token.IsCancellationRequested)
            {
                return;
            }
            _accounts = accounts;
            ViewerIsOwner = AdminAccountRules.IsOwner(accounts, ViewerId);
            LoadFailed = false;
            State = LibraryLoadState.Ready;
            Apply();
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex) && !token.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not read the accounts");
            await Task.WhenAll(capabilities, groups);
            if (token.IsCancellationRequested)
            {
                // A newer read took over while this one waited.
                return;
            }
            LoadFailed = true;
            // A list read before stays up under the failure, as the web's query keeps its data.
            State = _accounts.Count > 0 ? LibraryLoadState.Ready : LibraryLoadState.Error;
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // A newer read, or the page left.
        }
    }

    /// <summary>The page left: a read still running is dropped.</summary>
    public void Deactivate() => _load?.Cancel();

    [RelayCommand]
    private void OpenUser(AdminAccountRow? row)
    {
        if (row is not null)
        {
            UserRequested?.Invoke(this, row.Id);
        }
    }

    [RelayCommand]
    private async Task RetryGroupsAsync()
    {
        if (_actor is { } actor)
        {
            await ReadGroupsAsync(actor, _load?.Token ?? CancellationToken.None);
            Apply();
        }
    }

    private async Task ReadCapabilitiesAsync(AdminActor actor, CancellationToken token)
    {
        try
        {
            var capabilities = await _users.GetCapabilitiesAsync(actor, token);
            if (!token.IsCancellationRequested)
            {
                Capabilities = capabilities;
                IsAvailable = capabilities.Available;
            }
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            if (token.IsCancellationRequested)
            {
                return;
            }
            // As the web's: unknown is not available.
            _logger.LogWarning(ex, "Could not read what account administration offers");
            Capabilities = new AdminUserCapabilities();
            IsAvailable = false;
        }
    }

    private async Task ReadGroupsAsync(AdminActor actor, CancellationToken token)
    {
        try
        {
            var groups = await _users.ListAccessGroupsAsync(actor, token);
            if (!token.IsCancellationRequested)
            {
                Groups = groups;
                _groupNames = groups.ToDictionary(group => group.Id, group => group.Name, StringComparer.Ordinal);
                GroupsFailed = false;
                GroupFilters = [GroupFilters[0], .. groups.Select(group => new AdminGroupFilter(group.Id, group.Name)), DefaultFilters()[1]];
                SelectedGroup = GroupFilters.FirstOrDefault(filter => filter.Value == SelectedGroup.Value) ?? GroupFilters[0];
            }
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            if (token.IsCancellationRequested)
            {
                return;
            }
            _logger.LogWarning(ex, "Could not read the access groups");
            GroupsFailed = true;
        }
    }

    private static AdminGroupFilter[] DefaultFilters() =>
    [
        new(AdminGroupFilter.All, AppText.Get("web.admin.users.list.groupFilterAll")),
        new(AdminGroupFilter.None, AppText.Get("web.admin.users.list.noGroup")),
    ];
}
