using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Admin;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Admin;

/// <summary>The search, the group filter, the sort and the pages of the list, as the web's users table.</summary>
public sealed partial class AdminUsersViewModel
{
    /// <summary>The rows a page may show, as the web's page size picker.</summary>
    public static IReadOnlyList<int> PageSizes { get; } = [25, 50, 100];

    private int _pageIndex;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<AdminGroupFilter> GroupFilters { get; private set; }

    [ObservableProperty]
    public partial AdminGroupFilter? SelectedGroup { get; set; }

    [ObservableProperty]
    public partial AdminUserSort SortField { get; private set; } = AdminUserSort.Username;

    [ObservableProperty]
    public partial bool SortDescending { get; private set; }

    [ObservableProperty]
    public partial int PageSize { get; set; } = 25;

    /// <summary>The accounts of the page shown.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AdminAccountRow> Rows { get; private set; } = [];

    /// <summary>"Showing 1-25 of 140", when there is more than one page.</summary>
    [ObservableProperty]
    public partial string PageRangeText { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasPages { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    public partial bool CanGoBack { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    public partial bool CanGoOn { get; private set; }

    /// <summary>Accounts were read, and the search or filter leaves none.</summary>
    [ObservableProperty]
    public partial bool HasNoMatches { get; private set; }

    /// <summary>The search or filter leaves some accounts out: the empty list is theirs, not the server's.</summary>
    public string NoMatchesText => AppText.Get("web.admin.users.list.noMatches");

    public string PageSizeText(int size) => AppText.Get("web.admin.users.list.pageSizeOption", ("count", size));

    partial void OnSearchTextChanged(string value) => FirstPage();

    partial void OnSelectedGroupChanged(AdminGroupFilter? value) => FirstPage();

    partial void OnPageSizeChanged(int value) => FirstPage();

    /// <summary>A column's heading: sorts by it, or turns its order round when it already sorts.</summary>
    [RelayCommand]
    private void SortBy(AdminUserSort field)
    {
        if (field == SortField)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortField = field;
            // Newest first for dates, as the web's defaultSortDirection.
            SortDescending = field is AdminUserSort.Created or AdminUserSort.LastActive;
        }
        FirstPage();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PreviousPage()
    {
        _pageIndex--;
        Apply();
    }

    [RelayCommand(CanExecute = nameof(CanGoOn))]
    private void NextPage()
    {
        _pageIndex++;
        Apply();
    }

    private void FirstPage()
    {
        _pageIndex = 0;
        Apply();
    }

    // The web's filteredUsers, sortedUsers and paginatedUsers, in that order.
    private void Apply()
    {
        var shown = Sort(_accounts.Where(Matches)).ToList();
        int total = shown.Count;
        int size = PageSize > 0 ? PageSize : PageSizes[0];
        _pageIndex = Math.Clamp(_pageIndex, 0, Math.Max(0, (total - 1) / size));
        var now = _time.GetUtcNow();
        Rows = [.. shown.Skip(_pageIndex * size).Take(size).Select(user => AdminAccountRow.From(user, _groupNames, ViewerId, ViewerIsOwner, now))];
        HasPages = total > size;
        PageRangeText = HasPages
            ? AppText.Get("web.admin.users.list.pageRange", ("from", _pageIndex * size + 1), ("to", Math.Min((_pageIndex + 1) * size, total)), ("total", total))
            : "";
        CanGoBack = _pageIndex > 0;
        CanGoOn = (_pageIndex + 1) * size < total;
        HasNoMatches = State == LibraryLoadState.Ready && total == 0;
    }

    private bool Matches(AdminUser user)
    {
        string query = SearchText.Trim();
        if (query.Length > 0
            && !user.Username.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            && !user.Email.Contains(query, StringComparison.CurrentCultureIgnoreCase))
        {
            return false;
        }
        // None picked is all groups.
        return (SelectedGroup?.Value ?? AdminGroupFilter.All) switch
        {
            AdminGroupFilter.All => true,
            // Admin accounts can't join groups, so they show only under All groups.
            _ when user.IsAdmin => false,
            AdminGroupFilter.None => user.AccessGroupId is null,
            var group => user.AccessGroupId == group,
        };
    }

    // The web's sortAdminUsers: dates keep their own direction with the never-seen last, ties go by name, then by id.
    private IEnumerable<AdminUser> Sort(IEnumerable<AdminUser> users)
    {
        int direction = SortDescending ? -1 : 1;
        int Compare(AdminUser a, AdminUser b)
        {
            int result = SortField switch
            {
                AdminUserSort.Email => AdminAccountRow.CompareText(a.Email, b.Email) * direction,
                AdminUserSort.Role => AdminAccountRow.CompareText(RoleKey(a), RoleKey(b)) * direction,
                AdminUserSort.Status => AdminAccountRow.CompareText(StatusKey(a), StatusKey(b)) * direction,
                AdminUserSort.Created => CompareTime(a.CreatedAt, b.CreatedAt, direction),
                AdminUserSort.LastActive => CompareTime(a.LastActiveAt, b.LastActiveAt, direction),
                _ => AdminAccountRow.CompareText(a.Username, b.Username) * direction,
            };
            return result != 0 ? result
                : AdminAccountRow.CompareText(a.Username, b.Username) is var byName and not 0 ? byName
                : CompareIds(a.Id, b.Id);
        }
        var sorted = users.ToList();
        sorted.Sort(Compare);
        return sorted;
    }

    // The role as English words, so the order is the same in every language, as the web's englishText.
    private static string RoleKey(AdminUser user) =>
        AppText.English.Get("web.admin.common.accounts.role", [("role", user.IsOwner ? "owner" : user.Role)]);

    private static string StatusKey(AdminUser user) => user.Enabled ? "active" : "disabled";

    private static int CompareTime(DateTimeOffset? a, DateTimeOffset? b, int direction) => (a, b) switch
    {
        (null, null) => 0,
        (null, _) => 1,
        (_, null) => -1,
        var (x, y) => x.Value.CompareTo(y.Value) * direction,
    };

    private static int CompareIds(string a, string b) =>
        long.TryParse(a, out long x) && long.TryParse(b, out long y) ? x.CompareTo(y) : string.CompareOrdinal(a, b);
}
