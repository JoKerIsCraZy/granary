using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Admin;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Admin;

/// <summary>An account seen at the looked-up address: when first and last, and how many requests.</summary>
public sealed record AdminIpUserRow(string Username, string FirstSeen, string LastSeen, string Requests);

// The IP lookup: which accounts used an address over the last 30 days, as the web's.
public sealed partial class AdminActivityViewModel
{
    private const int IpDays = 30;
    private const int IpPageSize = 50;
    private string? _ipCursor;
    private int _ipLookups;

    /// <summary>The address to look up.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookupCommand))]
    public partial string IpQuery { get; set; } = "";

    /// <summary>The address the results are for.</summary>
    [ObservableProperty]
    public partial string LookedUpIp { get; private set; } = "";

    public ObservableCollection<AdminIpUserRow> IpUsers { get; } = [];

    /// <summary>"Searching...", "No users found for 203.0.113.50 in the last 30 days.", or a failure; empty with results.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIpMessage))]
    public partial string IpMessage { get; private set; } = "";

    [ObservableProperty]
    public partial bool IpFailed { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreIpUsersCommand))]
    public partial bool HasMoreIpUsers { get; private set; }

    public bool HasIpMessage => IpMessage.Length > 0;

    public bool HasIpUsers => IpUsers.Count > 0;

    [RelayCommand(CanExecute = nameof(CanLookup))]
    private Task LookupAsync()
    {
        LookedUpIp = IpQuery.Trim();
        IpUsers.Clear();
        _ipCursor = null;
        HasMoreIpUsers = false;
        OnPropertyChanged(nameof(HasIpUsers));
        IpMessage = "Searching...";
        IpFailed = false;
        return ReadIpUsersAsync();
    }

    private bool CanLookup() => IpQuery.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(HasMoreIpUsers))]
    private Task LoadMoreIpUsersAsync() => ReadIpUsersAsync();

    private async Task ReadIpUsersAsync()
    {
        if (_session.ActiveServer is not { } server || LookedUpIp.Length == 0)
        {
            return;
        }
        int lookup = ++_ipLookups;
        string ip = LookedUpIp;
        try
        {
            var page = await _admin.ListIpUsersAsync(server.BaseUri, ip, IpDays, IpPageSize, _ipCursor);
            if (lookup != _ipLookups)
            {
                return;
            }
            foreach (var user in page.Items)
            {
                IpUsers.Add(new AdminIpUserRow(user.Username.Length > 0 ? user.Username : $"User #{user.UserId}", Stamp(user.FirstSeen),
                    Stamp(user.LastSeen), AdminText.Count(user.RequestCount)));
            }
            _ipCursor = page.Page?.NextCursor;
            HasMoreIpUsers = page.Page is { HasMore: true, NextCursor.Length: > 0 };
            IpMessage = IpUsers.Count == 0 ? $"No users found for {ip} in the last {IpDays} days." : "";
            OnPropertyChanged(nameof(HasIpUsers));
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            if (lookup != _ipLookups)
            {
                return;
            }
            _logger.LogWarning(ex, "Could not look up an address's accounts");
            IpFailed = true;
            IpMessage = IpUsers.Count == 0 ? "Could not load IP history." : "Could not load more history.";
        }
    }

    // In the viewer's own time: "Oct 27, 2026, 8:41 PM".
    private string Stamp(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone).ToString("MMM d, yyyy, h:mm tt", CultureInfo.GetCultureInfo("en-US"));
}
