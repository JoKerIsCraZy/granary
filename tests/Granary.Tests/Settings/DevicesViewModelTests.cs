using System.Net;
using Granary.Core.Api.Accounts;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Profiles;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Settings;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.Settings;

/// <summary>Settings → Devices: the account's signed-in sessions, and the profile's devices.</summary>
public sealed class DevicesViewModelTests
{
    private const string Sessions = "api/v2/auth/sessions?limit=50";
    private const string SessionCapabilities = "api/v2/auth/sessions/capabilities";
    private const string Devices = "api/v2/devices?scope=profile&limit=200";
    private const string Capabilities = "api/v2/settings/contract/capabilities";
    private const string Profiles = "api/v2/profiles";

    private static readonly DateTimeOffset Now = new(2026, 10, 29, 18, 0, 0, TimeSpan.Zero);

    private static readonly ClientIdentity Identity = new("granary-pc", "PC", ClientIdentity.Platform, "Granary", "0.1.0", "dev", ClientIdentity.Family);

    private readonly ScriptedSilo _silo = new();
    private readonly FakeConfirmation _confirm = new();
    private readonly ManualTimeProvider _time = new(Now);
    private readonly FakeSessionManager _session = new()
    {
        State = SessionState.Ready,
        ActiveServer = ScriptedSilo.Server,
        ProfileId = "7",
        Account = new Account("1", "elena", "", "user", [], true, false, null),
    };

    private DevicesViewModel Page() => new(_session, new DevicesApi(_silo.Http), new SettingsApi(_silo.Http), new ProfilesApi(_silo.Http),
        Identity, _confirm, new ImmediateDispatcher(), _time, new LogSink().For<DevicesViewModel>());

    private static readonly Uri OtherBase = new("https://other.example/silo");

    // Another server picked in Settings → Servers while the page stays open. It knows this profile as an ordinary one, with
    // no devices yet.
    private void SwitchServer()
    {
        _silo.On(HttpMethod.Get, ScriptedSilo.Url(Profiles, OtherBase), ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "items": [ { "id": "7", "name": "Elena", "is_primary": false, "has_pin": false, "is_child": false } ], "avatar_upload_enabled": false }"""));
        _silo.On(HttpMethod.Get, ScriptedSilo.Url(Capabilities, OtherBase), ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "api_version": 1, "manifest_revision": 3, "supports_device_rename": true }"""));
        _silo.On(HttpMethod.Get, ScriptedSilo.Url(Devices, OtherBase), ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "items": [], "page": { "has_more": false } }"""));
        _session.ActiveServer = ScriptedSilo.Entry("srv2", OtherBase);
    }

    private void Primary(bool primary = true) =>
        _silo.On(HttpMethod.Get, Profiles, ScriptedSilo.Json(HttpStatusCode.OK,
            $$"""{ "items": [ { "id": "7", "name": "Elena", "is_primary": {{(primary ? "true" : "false")}}, "has_pin": false, "is_child": false } ], "avatar_upload_enabled": false }"""));

    private void Rename(bool supported = true) =>
        _silo.On(HttpMethod.Get, Capabilities, ScriptedSilo.Json(HttpStatusCode.OK,
            $$"""{ "api_version": 1, "manifest_revision": 3, "supports_device_rename": {{(supported ? "true" : "false")}} }"""));

    private void SessionsAvailable(bool available = true) =>
        _silo.On(HttpMethod.Get, SessionCapabilities, ScriptedSilo.Json(HttpStatusCode.OK,
            $$"""{ "state": "{{(available ? "available" : "unsupported")}}", "available": {{(available ? "true" : "false")}}, "last_seen": true, "admin_management": false }"""));

    private static string Session(string id, string name, bool current = false, string? platform = null, string? lastSeen = "2026-10-29T17:59:30Z") =>
        $$"""
        { "id": "{{id}}", "device_name": "{{name}}", "ip_address": "203.0.113.7", "created_at": "2026-10-01T08:00:00Z",
          "expires_at": "2026-11-30T08:00:00Z", {{(platform is null ? "" : $"\"device_platform\": \"{platform}\",")}}
          "last_seen_at": {{(lastSeen is null ? "null" : $"\"{lastSeen}\"")}}, "current": {{(current ? "true" : "false")}} }
        """;

    private void SessionList(string current, params string[] others) =>
        _silo.On(HttpMethod.Get, Sessions, ScriptedSilo.Json(HttpStatusCode.OK,
            $$"""{ "items": [ {{string.Join(",", [current, .. others])}} ], "page": { "has_more": false }, "current_session": {{current}} }"""));

    private static string Device(string id, string name, string platform, string lastSeen, bool current = false, int changed = 0, string? custom = null) =>
        $$"""
        { "device_id": "{{id}}", "device_name": "{{name}}", {{(custom is null ? "" : $"\"custom_name\": \"{custom}\",")}}
          "device_platform": "{{platform}}", "last_seen_at": "{{lastSeen}}", "profile_id": "7", "profile_name": "Elena",
          "is_current_device": {{(current ? "true" : "false")}}, "changed_count": {{changed}} }
        """;

    private void DeviceList(params string[] devices) =>
        _silo.On(HttpMethod.Get, Devices, ScriptedSilo.Json(HttpStatusCode.OK,
            $$"""{ "items": [ {{string.Join(",", devices)}} ], "page": { "has_more": false } }"""));

    private void Everything(bool primary = true)
    {
        Primary(primary);
        Rename();
        SessionsAvailable();
        SessionList(Session("s1", "Granary", current: true, platform: "Windows"),
            Session("s2", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36",
                lastSeen: "2026-10-29T15:00:00Z"));
        DeviceList(Device("d1", "Granary", "Windows", "2026-10-29T17:59:00Z", current: true),
            Device("d2", "Apple TV", "tvOS", "2026-10-27T18:00:00Z", changed: 3, custom: "Living Room"),
            Device("d3", "iPhone", "iOS", "2026-09-01T18:00:00Z", changed: 1));
    }

    [Fact]
    public async Task ThisDevicesSession_StandsApart_AndTheOthersFollow()
    {
        Everything();
        var page = Page();

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(page.ShowsSessions);
        var current = Assert.IsType<SignedInSessionRow>(page.CurrentSession);
        Assert.Equal(("Granary", true, "Sign out this device"), (current.Name, current.IsCurrent, current.SignOutLabel));
        Assert.Equal("Active now · Signed in Oct 1, 2026, 8:00 AM", current.StatusLine);
        var other = Assert.Single(page.OtherSessions);
        Assert.Equal(("Chrome on Windows", SessionDeviceKind.Desktop, "Sign out"), (other.Name, other.Kind, other.SignOutLabel));
        Assert.Equal("Last seen 3 hr ago · Signed in Oct 1, 2026, 8:00 AM", other.StatusLine);
        Assert.Equal("203.0.113.7", other.IpAddress);
        Assert.False(page.HasNoOtherSessions);
    }

    [Fact]
    public async Task AProfileThatIsNotTheHouseholds_SeesItsDevices_ButNoSessions()
    {
        Everything(primary: false);
        var page = Page();

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(page.ShowsSessions);
        Assert.Empty(_silo.SentTo(HttpMethod.Get, Sessions));
        Assert.Equal(3, page.DeviceGroups.Sum(g => g.Devices.Count));
    }

    [Fact]
    public async Task AnAdministratorWithoutAProfile_SeesTheSessions()
    {
        Everything(primary: false);
        _session.ProfileId = null;
        _session.Account = new Account("1", "elena", "", "admin", [], true, false, null);
        var page = Page();

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(page.ShowsSessions);
    }

    [Fact]
    public async Task AServerWithoutSessionManagement_SaysSo()
    {
        Everything();
        SessionsAvailable(available: false);
        var page = Page();

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Signed-in session management is unavailable on this server.", page.SessionsMessage);
        Assert.Null(page.CurrentSession);
        Assert.Empty(_silo.SentTo(HttpMethod.Get, Sessions));
    }

    [Fact]
    public async Task SigningOutAnotherSession_AsksFirst_AndTakesItOffTheList()
    {
        Everything();
        _silo.On(HttpMethod.Delete, "api/v2/auth/sessions/s2", ScriptedSilo.NoContent());
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);

        await page.SignOutSessionCommand.ExecuteAsync(page.OtherSessions[0]);

        var asked = Assert.Single(_confirm.Asked);
        Assert.Equal(("Sign out Chrome on Windows?", "Sign out"), (asked.Title, asked.Action));
        Assert.StartsWith("This client will lose access on its next authenticated request.", asked.Message, StringComparison.Ordinal);
        Assert.Single(_silo.SentTo(HttpMethod.Delete, "api/v2/auth/sessions/s2"));
        Assert.Empty(page.OtherSessions);
        Assert.True(page.HasNoOtherSessions);
        Assert.Equal("Session signed out", page.Status);
    }

    [Fact]
    public async Task ANoToTheQuestion_SignsNothingOut()
    {
        Everything();
        _confirm.Answer = false;
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);

        await page.SignOutSessionCommand.ExecuteAsync(page.OtherSessions[0]);

        Assert.Empty(_silo.SentTo(HttpMethod.Delete, "api/v2/auth/sessions/s2"));
        Assert.Single(page.OtherSessions);
    }

    [Fact]
    public async Task ASignOutTheServerRefuses_KeepsTheSession_AndSaysSo()
    {
        Everything();
        _silo.On(HttpMethod.Delete, "api/v2/auth/sessions/s2", ScriptedSilo.Problem(500, "internal_error"));
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);

        await page.SignOutSessionCommand.ExecuteAsync(page.OtherSessions[0]);

        Assert.Single(page.OtherSessions);
        Assert.Equal("Couldn't sign out the session. Try again.", page.Status);
    }

    [Fact]
    public async Task SigningOutThisDevice_SignsGranaryOutOfTheServer()
    {
        Everything();
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);

        await page.SignOutSessionCommand.ExecuteAsync(page.CurrentSession);

        Assert.Equal("Sign out this device?", Assert.Single(_confirm.Asked).Title);
        Assert.Contains("sign-out:srv1", _session.Calls);
        Assert.Empty(_silo.SentTo(HttpMethod.Delete, "api/v2/auth/sessions/s1"));
    }

    [Fact]
    public async Task AfterAnotherServerIsPicked_SignOutSignsNothingOut_AndTheListsAreReadAgain()
    {
        Everything();
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);
        var other = page.OtherSessions[0];
        var current = page.CurrentSession;
        SwitchServer();

        await page.SignOutSessionCommand.ExecuteAsync(other);
        await page.SignOutSessionCommand.ExecuteAsync(current);

        Assert.Empty(_confirm.Asked);
        Assert.DoesNotContain(_silo.Sent, r => r.Method == HttpMethod.Delete);
        Assert.DoesNotContain(_session.Calls, c => c.StartsWith("sign-out", StringComparison.Ordinal));
        Assert.Equal("The server or profile changed. The lists were read again.", page.Status);
        Assert.Contains(_silo.Sent, r => r.Method == HttpMethod.Get && r.Uri.Host == OtherBase.Host);
        Assert.Equal((false, null, true), (page.ShowsSessions, page.CurrentSession, page.HasNoDevices));
    }

    [Fact]
    public async Task AnotherServerPickedWhileTheQuestionIsOpen_SignsNothingOut()
    {
        Everything();
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);
        _confirm.WhileAsking = () =>
        {
            SwitchServer();
            return Task.CompletedTask;
        };

        await page.SignOutSessionCommand.ExecuteAsync(page.CurrentSession);

        Assert.Equal("Sign out this device?", Assert.Single(_confirm.Asked).Title);
        Assert.DoesNotContain(_silo.Sent, r => r.Method != HttpMethod.Get);
        Assert.DoesNotContain(_session.Calls, c => c.StartsWith("sign-out", StringComparison.Ordinal));
        Assert.Equal("The server or profile changed. The lists were read again.", page.Status);
    }

    [Fact]
    public async Task AfterAnotherProfileIsPicked_ForgetAndUseProfileSettings_SendNothing()
    {
        Everything();
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);
        var tv = page.DeviceGroups[1].Devices.Single();
        var phone = page.DeviceGroups[2].Devices.Single();
        _session.ProfileId = "8";

        await page.ForgetCommand.ExecuteAsync(phone);
        await page.UseProfileSettingsCommand.ExecuteAsync(tv);
        phone.StartRenameCommand.Execute(null);
        phone.EditName = "Elena's phone";
        await page.SaveRenameCommand.ExecuteAsync(phone);

        Assert.Empty(_confirm.Asked);
        Assert.DoesNotContain(_silo.Sent, r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task AnotherProfilePickedWhileThePageIsOpen_ReadsTheListsAgainForIt()
    {
        Everything();
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(page.CurrentSession);

        _session.ProfileId = "8";
        _session.RaiseChanged();
        await ServerPlaybackFixture.Until(() => !page.ShowsSessions && page.CurrentSession is null, "the lists read for the new profile");

        Assert.Null(page.CurrentSession);
        Assert.Empty(page.OtherSessions);
        page.Dispose();
    }

    [Fact]
    public async Task AReadOfTheSessionsThatFails_LeavesNoOldSessionToSignOut()
    {
        Everything();
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);
        _silo.On(HttpMethod.Get, Sessions, ScriptedSilo.Problem(500, "internal_error"));

        await page.RefreshCommand.ExecuteAsync(null);

        Assert.Null(page.CurrentSession);
        Assert.Empty(page.OtherSessions);
        Assert.Equal("Couldn't load sessions. Try again.", page.SessionsMessage);
        Assert.True(page.CanRetrySessions);
    }

    [Fact]
    public async Task AProfileReadThatFails_SaysSo_RatherThanHidingTheSessions()
    {
        Everything();
        _silo.On(HttpMethod.Get, Profiles, ScriptedSilo.Problem(500, "internal_error"));
        var page = Page();

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(page.ShowsSessions);
        Assert.Equal("Couldn't load sessions. Try again.", page.SessionsMessage);
        Assert.True(page.CanRetrySessions);
        Assert.Empty(_silo.SentTo(HttpMethod.Get, Sessions));
    }

    [Fact]
    public async Task ASessionAlreadyGone_LeavesTheList()
    {
        Everything();
        _silo.On(HttpMethod.Delete, "api/v2/auth/sessions/s2", ScriptedSilo.Problem(404, "not_found"));
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);

        await page.SignOutSessionCommand.ExecuteAsync(page.OtherSessions[0]);

        Assert.Empty(page.OtherSessions);
        Assert.Equal("Session signed out", page.Status);
    }

    [Fact]
    public async Task ADeviceAlreadyForgotten_LeavesTheList()
    {
        Everything();
        _silo.On(HttpMethod.Delete, "api/v2/devices/d3", ScriptedSilo.Problem(404, "not_found"));
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);

        await page.ForgetCommand.ExecuteAsync(page.DeviceGroups[2].Devices.Single());

        Assert.Equal(["Using now", "This week"], page.DeviceGroups.Select(g => g.Title));
        Assert.Equal("Device forgotten", page.Status);
    }

    [Fact]
    public async Task ADeviceGoneWhenItsSettingsAreCleared_IsReadAgain()
    {
        Everything();
        _silo.On(HttpMethod.Delete, "api/v2/devices/d2/settings", ScriptedSilo.Problem(404, "not_found"));
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);
        DeviceList(Device("d1", "Granary", "Windows", "2026-10-29T17:59:00Z", current: true));

        await page.UseProfileSettingsCommand.ExecuteAsync(page.DeviceGroups[1].Devices.Single());

        Assert.Equal("That device is no longer listed.", page.Status);
        Assert.Equal(["Using now"], page.DeviceGroups.Select(g => g.Title));
    }

    [Fact]
    public async Task ADeviceGoneWhenItIsRenamed_IsReadAgain()
    {
        Everything();
        _silo.On(HttpMethod.Patch, "api/v2/devices/d3", ScriptedSilo.Problem(404, "not_found"));
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);
        var phone = page.DeviceGroups[2].Devices.Single();
        DeviceList(Device("d1", "Granary", "Windows", "2026-10-29T17:59:00Z", current: true));

        phone.StartRenameCommand.Execute(null);
        phone.EditName = "Elena's phone";
        await page.SaveRenameCommand.ExecuteAsync(phone);

        Assert.Single(_silo.SentTo(HttpMethod.Patch, "api/v2/devices/d3"));
        Assert.Equal("That device is no longer listed.", page.Status);
        Assert.Equal(["Using now"], page.DeviceGroups.Select(g => g.Title));
    }

    [Fact]
    public async Task ARowLeftFromAnEarlierRead_SendsNothing()
    {
        Everything();
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);
        var phone = page.DeviceGroups[2].Devices.Single();
        var other = page.OtherSessions[0];

        await page.RefreshCommand.ExecuteAsync(null);
        await page.ForgetCommand.ExecuteAsync(phone);
        await page.SignOutSessionCommand.ExecuteAsync(other);

        Assert.Empty(_confirm.Asked);
        Assert.DoesNotContain(_silo.Sent, r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task ADeviceWithThisPcsDeviceId_IsThisDevice_EvenWhenTheServerDoesNotSaySo()
    {
        Everything();
        DeviceList(Device("granary-pc", "PC", "Windows", "2026-10-29T17:59:00Z"),
            Device("d2", "Apple TV", "tvOS", "2026-10-27T18:00:00Z"));
        var page = Page();

        await page.LoadAsync(TestContext.Current.CancellationToken);

        var mine = page.DeviceGroups[0].Devices.Single();
        Assert.Equal(("Using now", true, false), (page.DeviceGroups[0].Title, mine.IsCurrent, mine.CanForget));
    }

    [Fact]
    public async Task TheDeviceList_IsAskedForWithThisPcsDeviceId()
    {
        DeviceList();
        var http = new Granary.Core.Api.SiloHttp(new HttpClient(new Granary.Core.Http.ClientIdentityHandler(Identity) { InnerHandler = _silo.Handler }));

        await new DevicesApi(http).ListDevicesAsync(ScriptedSilo.Base, TestContext.Current.CancellationToken);

        Assert.Equal("granary-pc", Assert.Single(_silo.SentTo(HttpMethod.Get, Devices)).DeviceId);
    }

    [Fact]
    public async Task MoreSessions_ComeFromTheNextPage()
    {
        Everything();
        // One route answers both pages, in order: the scripted server tells requests apart by path only.
        _silo.On(HttpMethod.Get, Sessions,
            ScriptedSilo.Json(HttpStatusCode.OK,
                $$"""{ "items": [ {{Session("s1", "Granary", current: true)}} ], "page": { "has_more": true, "next_cursor": "c2" }, "current_session": {{Session("s1", "Granary", current: true)}} }"""),
            ScriptedSilo.Json(HttpStatusCode.OK,
                $$"""{ "items": [ {{Session("s9", "Granary on the laptop", platform: "Windows")}} ], "page": { "has_more": false }, "current_session": null }"""));
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(page.CanLoadMoreSessions);
        Assert.Empty(page.OtherSessions);

        await page.LoadMoreSessionsCommand.ExecuteAsync(null);

        Assert.EndsWith("api/v2/auth/sessions?limit=50&cursor=c2", _silo.SentTo(HttpMethod.Get, Sessions)[1].Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("Granary on the laptop", Assert.Single(page.OtherSessions).Name);
        Assert.False(page.CanLoadMoreSessions);
    }

    [Fact]
    public async Task Devices_AreGroupedByWhenTheyWereUsed()
    {
        Everything();
        var page = Page();

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Using now", "This week", "Earlier"], page.DeviceGroups.Select(g => g.Title));
        var current = page.DeviceGroups[0].Devices.Single();
        Assert.Equal(("Granary", "Desktop · using now · nothing changed here", false, false), (current.Name, current.Line, current.CanForget, current.CanUseProfileSettings));
        var tv = page.DeviceGroups[1].Devices.Single();
        Assert.Equal(("Living Room", "Reports itself as Apple TV", "TV · 2 days ago · 3 things set differently"), (tv.Name, tv.ReportedName, tv.Line));
        Assert.True(tv.CanForget);
        Assert.True(tv.CanUseProfileSettings);
        Assert.Equal("Mobile · 2 months ago · 1 thing set differently", page.DeviceGroups[2].Devices.Single().Line);
    }

    [Fact]
    public async Task NoDevices_SaySo()
    {
        Everything();
        DeviceList();
        var page = Page();

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(page.DeviceGroups);
        Assert.True(page.HasNoDevices);
    }

    [Fact]
    public async Task ADeviceGetsAName_AndAnEmptyNameGivesBackTheOneItReports()
    {
        Everything();
        _silo.On(HttpMethod.Patch, "api/v2/devices/d3",
            ScriptedSilo.Json(HttpStatusCode.OK, Device("d3", "iPhone", "iOS", "2026-09-01T18:00:00Z", changed: 1, custom: "Elena's phone")),
            ScriptedSilo.Json(HttpStatusCode.OK, Device("d3", "iPhone", "iOS", "2026-09-01T18:00:00Z", changed: 1)));
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);
        var phone = page.DeviceGroups[2].Devices.Single();
        Assert.True(page.CanRename);

        phone.StartRenameCommand.Execute(null);
        Assert.True(phone.IsRenaming);
        phone.EditName = "  Elena's phone ";
        await page.SaveRenameCommand.ExecuteAsync(phone);

        Assert.Equal("Elena's phone", NameSent(0));
        Assert.Equal(("Elena's phone", false, "Device renamed"), (phone.Name, phone.IsRenaming, page.Status));

        phone.StartRenameCommand.Execute(null);
        phone.EditName = "";
        await page.SaveRenameCommand.ExecuteAsync(phone);

        Assert.Equal("""{"custom_name":null}""", _silo.SentTo(HttpMethod.Patch, "api/v2/devices/d3")[1].Body);
        Assert.Equal(("iPhone", "Device name reset"), (phone.Name, page.Status));
    }

    // The name a rename sent; the body escapes some characters, so it is read rather than compared as text.
    private string? NameSent(int index)
    {
        using var body = System.Text.Json.JsonDocument.Parse(_silo.SentTo(HttpMethod.Patch, "api/v2/devices/d3")[index].Body!);
        return body.RootElement.GetProperty("custom_name").GetString();
    }

    [Fact]
    public async Task AServerThatCannotRename_OffersNoRename()
    {
        Everything();
        Rename(supported: false);
        var page = Page();

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(page.CanRename);
    }

    [Fact]
    public async Task UseProfileSettings_AsksFirst_AndClearsWhatTheDeviceChanged()
    {
        Everything();
        _silo.On(HttpMethod.Delete, "api/v2/devices/d2/settings", ScriptedSilo.NoContent());
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);
        var tv = page.DeviceGroups[1].Devices.Single();
        DeviceList(Device("d1", "Granary", "Windows", "2026-10-29T17:59:00Z", current: true),
            Device("d2", "Apple TV", "tvOS", "2026-10-27T18:00:00Z", custom: "Living Room"));

        await page.UseProfileSettingsCommand.ExecuteAsync(tv);

        var asked = Assert.Single(_confirm.Asked);
        Assert.Equal("Use your profile settings on Living Room?", asked.Title);
        Assert.Equal("This removes the 3 settings changed on this device. Settings that only apply to devices go back to the app default.", asked.Message);
        Assert.Single(_silo.SentTo(HttpMethod.Delete, "api/v2/devices/d2/settings"));
        Assert.Equal("Removed the changes on this device", page.Status);
        Assert.Equal("TV · 2 days ago · nothing changed here", page.DeviceGroups[1].Devices.Single().Line);
    }

    [Fact]
    public async Task Forget_AsksFirst_AndTakesTheDeviceOffTheList()
    {
        Everything();
        _silo.On(HttpMethod.Delete, "api/v2/devices/d3", ScriptedSilo.NoContent());
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);
        var phone = page.DeviceGroups[2].Devices.Single();

        await page.ForgetCommand.ExecuteAsync(phone);

        var asked = Assert.Single(_confirm.Asked);
        Assert.Equal(("Forget iPhone?", "Its settings are removed and it disappears from this list until it's used again.", "Forget"),
            (asked.Title, asked.Message, asked.Action));
        Assert.Equal(["Using now", "This week"], page.DeviceGroups.Select(g => g.Title));
        Assert.Equal("Device forgotten", page.Status);
    }

    [Fact]
    public async Task AForgetTheServerRefuses_KeepsTheDevice()
    {
        Everything();
        _silo.On(HttpMethod.Delete, "api/v2/devices/d3", ScriptedSilo.Problem(500, "internal_error"));
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);

        await page.ForgetCommand.ExecuteAsync(page.DeviceGroups[2].Devices.Single());

        Assert.Equal(3, page.DeviceGroups.Count);
        Assert.Equal("Couldn't forget", page.Status);
    }

    [Fact]
    public async Task ThisDevice_CannotBeForgottenFromHere()
    {
        Everything();
        var page = Page();
        await page.LoadAsync(TestContext.Current.CancellationToken);

        await page.ForgetCommand.ExecuteAsync(page.DeviceGroups[0].Devices.Single());

        Assert.Empty(_confirm.Asked);
    }

    [Theory]
    [InlineData("Granary", "Windows", "Granary", SessionDeviceKind.Desktop)]
    [InlineData("Living Room Apple TV", "tvOS", "Living Room Apple TV", SessionDeviceKind.Tv)]
    [InlineData("", "iOS", "Unknown device", SessionDeviceKind.Phone)]
    [InlineData("", null, "Unknown device", SessionDeviceKind.Unknown)]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15", null,
        "Safari on macOS", SessionDeviceKind.Desktop)]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0", null, "Firefox on Linux", SessionDeviceKind.Desktop)]
    [InlineData("okhttp/4.12.0", null, "okhttp/4.12.0", SessionDeviceKind.Unknown)]
    [InlineData("Silo/2.1 (iPhone; iOS 18.0)", null, "Silo app on iPhone", SessionDeviceKind.Phone)]
    [InlineData("Elena's laptop", null, "Elena's laptop", SessionDeviceKind.Unknown)]
    public void ASession_IsNamedAsTheWebNamesIt(string deviceName, string? platform, string name, SessionDeviceKind kind)
    {
        Assert.Equal((name, kind), DeviceText.SessionDevice(deviceName, platform));
    }

    [Theory]
    [InlineData(null, "Last seen not recorded yet")]
    [InlineData(30, "Active now")]
    [InlineData(5 * 60, "Last seen 5 min ago")]
    [InlineData(3 * 3600, "Last seen 3 hr ago")]
    [InlineData(30 * 3600, "Last seen 1 day ago")]
    [InlineData(2 * 86400, "Last seen 2 days ago")]
    public void ASessionsLastSeen_ReadsAsTheWebs(int? secondsAgo, string text)
    {
        Assert.Equal(text, DeviceText.SessionLastSeen(secondsAgo is { } s ? Now.AddSeconds(-s) : null, Now));
    }

    [Theory]
    [InlineData(10 * 60, "Less than an hour ago")]
    [InlineData(3600, "1 hour ago")]
    [InlineData(5 * 3600, "5 hours ago")]
    [InlineData(30 * 3600, "Yesterday")]
    [InlineData(5 * 86400, "5 days ago")]
    [InlineData(45 * 86400, "2 months ago")]
    [InlineData(400 * 86400, "1 year ago")]
    public void ADevicesLastSeen_ReadsAsTheWebs(int secondsAgo, string text)
    {
        Assert.Equal(text, DeviceText.DeviceLastSeen(Now.AddSeconds(-secondsAgo), Now));
    }

    [Theory]
    [InlineData("Windows", "Desktop")]
    [InlineData("tvOS", "TV")]
    [InlineData("iPadOS", "Tablet")]
    [InlineData("Android", "Mobile")]
    [InlineData("", "Other")]
    [InlineData("PlayStation", "Other")]
    public void APlatform_IsTheWebsKind(string platform, string kind)
    {
        Assert.Equal(kind, DeviceText.PlatformKind(platform));
    }
}
