using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Accounts;
using Granary.Core.Texts;

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
        IsActiveNow = DeviceText.IsActiveNow(session.LastSeenAt, now);
        SignedInDetail = DeviceText.Stamp(session.CreatedAt, zone);
        StatusRuns = AppText.Runs("web.settings.sessions.row.summary", ("last_seen", LastSeenText), ("signed_in", SignedInDetail));
        StatusLine = string.Concat(StatusRuns.Select(run => run.Text));
        LastSeenDetail = session.LastSeenAt is { } seen ? DeviceText.Stamp(seen, zone) : AppText.Get("web.settings.sessions.row.notRecorded");
        IpAddress = session.IpAddress.Length > 0 ? session.IpAddress : AppText.Get("web.settings.sessions.row.unknown");
        ExpiresDetail = DeviceText.Stamp(session.ExpiresAt, zone);
        UserAgent = session.DeviceName.Length > 0 ? session.DeviceName : AppText.Get("web.settings.sessions.row.notSent");
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

    /// <summary>
    /// <see cref="StatusLine"/> as the web's message gives it: the part tagged <c>status</c> is <see cref="LastSeenText"/>,
    /// which may show in green, wherever the language puts it.
    /// </summary>
    public IReadOnlyList<TextRun> StatusRuns { get; }

    public string LastSeenDetail { get; }

    public string SignedInDetail { get; }

    /// <summary>The address it signed in from.</summary>
    public string IpAddress { get; }

    public string ExpiresDetail { get; }

    /// <summary>The name or User-Agent it signed in with, as sent.</summary>
    public string UserAgent { get; }

    public string SignOutLabel => IsCurrent ? AppText.Get("granary.settings.sessions.signOutThisDevice") : AppText.Get("common.signOut");

    public string SignOutName =>
        IsCurrent ? AppText.Get("granary.settings.sessions.signOutThisDevice") : AppText.Get("web.settings.sessions.row.signOutDevice", ("device", Name));

    public string DetailsName => AppText.Get("web.settings.sessions.row.detailsFor", ("device", Name));

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

    /// <param name="isCurrent">This PC: the server marks it, or it carries this installation's device id.</param>
    internal UserDeviceRow(UserDevice device, bool isCurrent, DateTimeOffset now)
    {
        _now = now;
        DeviceId = device.DeviceId;
        IsCurrent = isCurrent;
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

    public string RenameName => AppText.Get("granary.settings.devices.renameName", ("device", Name));

    public string ForgetName => AppText.Get("granary.settings.devices.forgetName", ("device", Name));

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
            ? AppText.Get("web.settings.device.reportsItselfAs", ("name", device.DeviceName))
            : "";
        string used = IsCurrent ? AppText.Get("web.settings.device.usingNow") : DeviceText.DeviceLastSeen(device.LastSeenAt, _now);
        int count = device.ChangedCount;
        string changed = count > 0
            ? AppText.Get("web.settings.device.changedCount", ("count", count), ("count_text", AppText.Number(count)))
            : AppText.Get("web.settings.device.nothingChanged");
        // Three parts the web joins with a middle dot, as it does.
        Line = string.Join(" · ", DeviceText.KindLabel(DeviceText.PlatformKind(device.DevicePlatform)), used, changed);
        CanUseProfileSettings = device.ChangedCount > 0;
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(RenameName));
        OnPropertyChanged(nameof(ForgetName));
    }

    /// <summary>The name the profile gave it, else the one it reports.</summary>
    internal static string DisplayName(UserDevice device) =>
        device.CustomName is { Length: > 0 } custom ? custom : device.DeviceName.Length > 0 ? device.DeviceName : DeviceText.UnknownDevice;
}
