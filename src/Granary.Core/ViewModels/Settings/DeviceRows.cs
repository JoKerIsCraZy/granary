using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Accounts;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// A sign-in of the account, as the web's <c>LoginSessionRow</c>: its device's name and when it was last seen, and behind
/// Details the dates, the address it signed in from, and what it reported itself as.
/// </summary>
public sealed partial class SignedInSessionRow : ObservableObject
{
    internal SignedInSessionRow(LoginSession session, DateTimeOffset now, TimeZoneInfo zone)
    {
        Id = session.Id;
        (Name, Kind) = DeviceText.SessionDevice(session.DeviceName, session.DevicePlatform);
        IsCurrent = session.Current;
        LastSeenText = DeviceText.SessionLastSeen(session.LastSeenAt, now);
        IsActiveNow = LastSeenText == "Active now";
        SignedInLine = $"· Signed in {DeviceText.Stamp(session.CreatedAt, zone)}";
        StatusLine = $"{LastSeenText} {SignedInLine}";
        LastSeenDetail = session.LastSeenAt is { } seen ? DeviceText.Stamp(seen, zone) : "Not recorded yet";
        SignedInDetail = DeviceText.Stamp(session.CreatedAt, zone);
        IpAddress = session.IpAddress.Length > 0 ? session.IpAddress : "Unknown";
        ExpiresDetail = DeviceText.Stamp(session.ExpiresAt, zone);
        UserAgent = session.DeviceName.Length > 0 ? session.DeviceName : "Not sent";
    }

    public string Id { get; }

    public string Name { get; }

    public SessionDeviceKind Kind { get; }

    /// <summary>Granary's own sign-in on this PC.</summary>
    public bool IsCurrent { get; }

    /// <summary>"Active now", "Last seen 5 min ago", …</summary>
    public string LastSeenText { get; }

    /// <summary>Seen within two minutes: the web shows it in green.</summary>
    public bool IsActiveNow { get; }

    /// <summary>"Last seen 3 hr ago · Signed in Oct 1, 2026, 8:00 AM".</summary>
    public string StatusLine { get; }

    /// <summary>"· Signed in Oct 1, 2026, 8:00 AM": the line after <see cref="LastSeenText"/>, which may show in green.</summary>
    public string SignedInLine { get; }

    public string LastSeenDetail { get; }

    public string SignedInDetail { get; }

    /// <summary>The address it signed in from.</summary>
    public string IpAddress { get; }

    public string ExpiresDetail { get; }

    /// <summary>The name or User-Agent it signed in with, as sent.</summary>
    public string UserAgent { get; }

    public string SignOutLabel => IsCurrent ? "Sign out this device" : "Sign out";

    public string SignOutName => IsCurrent ? "Sign out this device" : $"Sign out {Name}";

    public string DetailsName => $"Details for {Name}";

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>A sign-out on its way.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; internal set; }

    [RelayCommand]
    private void ToggleDetails() => IsExpanded = !IsExpanded;
}

/// <summary>Devices used in the same span: "Using now", "This week", "Earlier", as the web's device list groups them.</summary>
public sealed record UserDeviceGroup(string Title, ObservableCollection<UserDeviceRow> Devices);

/// <summary>
/// A device the profile watches on: its name (the profile's own, or the one it reports), what it is and when it was used,
/// and how many settings are set differently there, as the web's Your Devices heads a device.
/// </summary>
public sealed partial class UserDeviceRow : ObservableObject
{
    private readonly DateTimeOffset _now;

    internal UserDeviceRow(UserDevice device, DateTimeOffset now)
    {
        _now = now;
        DeviceId = device.DeviceId;
        IsCurrent = device.IsCurrentDevice;
        Show(device);
    }

    public string DeviceId { get; }

    /// <summary>Granary on this PC: it can't be forgotten from here.</summary>
    public bool IsCurrent { get; }

    [ObservableProperty]
    public partial UserDevice Device { get; private set; } = null!;

    /// <summary>The name the profile gave it, else the one it reports.</summary>
    [ObservableProperty]
    public partial string Name { get; private set; } = "";

    /// <summary>"Reports itself as Apple TV" while it has a name of the profile's own; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReportedName))]
    public partial string ReportedName { get; private set; } = "";

    public bool HasReportedName => ReportedName.Length > 0;

    /// <summary>"TV · 2 days ago · 3 things set differently".</summary>
    [ObservableProperty]
    public partial string Line { get; private set; } = "";

    [ObservableProperty]
    public partial bool CanUseProfileSettings { get; private set; }

    public bool CanForget => !IsCurrent;

    public string Kind => DeviceText.PlatformKind(Device.DevicePlatform);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRenaming), nameof(ShowsRename))]
    public partial bool IsRenaming { get; private set; }

    public bool IsNotRenaming => !IsRenaming;

    /// <summary>The server can name devices (<c>supports_device_rename</c>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRename))]
    public partial bool CanRename { get; internal set; }

    /// <summary>Rename shows: the server names devices, and no name is being typed.</summary>
    public bool ShowsRename => CanRename && !IsRenaming;

    /// <summary>The name being typed; empty shows the reported name again.</summary>
    [ObservableProperty]
    public partial string EditName { get; set; } = "";

    /// <summary>A change on its way.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; internal set; }

    public string RenameName => $"Rename {Name}";

    public string ForgetName => $"Forget {Name}";

    [RelayCommand]
    private void StartRename()
    {
        EditName = Device.CustomName ?? "";
        IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    internal void EndRename() => IsRenaming = false;

    internal void Show(UserDevice device)
    {
        Device = device;
        Name = DisplayName(device);
        ReportedName = device.CustomName is { Length: > 0 } custom && device.DeviceName.Length > 0 && custom != device.DeviceName
            ? "Reports itself as " + device.DeviceName
            : "";
        string used = IsCurrent ? "using now" : DeviceText.DeviceLastSeen(device.LastSeenAt, _now);
        string changed = device.ChangedCount > 0 ? DeviceText.Count(device.ChangedCount, "thing") + " set differently" : "nothing changed here";
        Line = $"{DeviceText.PlatformKind(device.DevicePlatform)} · {used} · {changed}";
        CanUseProfileSettings = device.ChangedCount > 0;
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(RenameName));
        OnPropertyChanged(nameof(ForgetName));
    }

    /// <summary>The name the profile gave it, else the one it reports.</summary>
    internal static string DisplayName(UserDevice device) =>
        device.CustomName is { Length: > 0 } custom ? custom : device.DeviceName.Length > 0 ? device.DeviceName : "Unknown device";
}
