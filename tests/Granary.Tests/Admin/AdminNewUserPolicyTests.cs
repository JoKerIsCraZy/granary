using System.Net;
using System.Text.Json;
using Granary.Core.Api.Admin;
using Granary.Core.ViewModels.Admin;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;

namespace Granary.Tests.Admin;

/// <summary>
/// Admin → Users → Add User, its Access and Limits tabs as the web's CreateUserForm: only what is overridden is sent, and
/// each default names where it comes from and what it is now.
/// </summary>
public sealed class AdminNewUserPolicyTests
{
    private static readonly AdminAccessGroup[] Groups =
    [
        new() { Id = "10", Name = "Streamers", IsDefault = true, LibraryIds = ["5", "6"], MaxStreams = 0, MaxProfiles = 5, DownloadAllowed = false,
            RequestsAllowed = true, TranscodeAllowed = true, MaxRemoteStreamBitrateKbps = 8000 },
        new() { Id = "11", Name = "Friends", MaxStreams = 2, MaxProfiles = 3 },
    ];

    private const string Defaults = """
        { "admin": { "library_ids": null, "max_playback_quality": "", "max_streams": 0, "max_transcodes": 0, "max_remote_stream_bitrate_kbps": 0,
                     "max_local_stream_bitrate_kbps": 0, "max_profiles": 10, "transcode_allowed": true, "audio_transcode_allowed": true,
                     "download_allowed": true, "download_transcode_allowed": true, "requests_allowed": true },
          "ungrouped": { "library_ids": [], "max_playback_quality": "1080p", "max_streams": 1, "max_transcodes": 1, "max_remote_stream_bitrate_kbps": 0,
                     "max_local_stream_bitrate_kbps": 0, "max_profiles": 5, "transcode_allowed": false, "audio_transcode_allowed": false,
                     "download_allowed": false, "download_transcode_allowed": false, "requests_allowed": false } }
        """;

    private readonly BrowseFakeSilo _silo = AdminUserPayloads.Silo();
    private readonly FakeSessionManager _session = AdminUserPayloads.OwnerSession();

    public AdminNewUserPolicyTests()
    {
        _silo.Map("api/v2/admin/users", _ => BrowseFakeSilo.Json("""{ "id": "9" }""", HttpStatusCode.Created));
        _silo.Map("api/v2/admin/users/policy-defaults", Defaults);
        _silo.Map("api/v2/admin/settings/effective", """{ "download.enabled": "true", "download.transcode_enabled": "false", "signup.enabled": "true" }""");
        _silo.Map("api/v2/libraries", """
            { "items": [ { "id": "5", "name": "Movies", "type": "movies", "enabled": true, "sort_order": 0 },
              { "id": "6", "name": "Shows", "type": "shows", "enabled": false, "sort_order": 1 } ] }
            """);
    }

    private async Task<AdminNewUserViewModel> StartedAsync()
    {
        var form = new AdminNewUserViewModel(new AdminUsersApi(_silo.Http), new AdminApi(_silo.Http), _silo.Logs.For<AdminNewUserViewModel>());
        form.Start(AdminActors.Of(_session), new AdminUserCapabilities { Available = true, DefaultProfile = true }, Groups, groupsKnown: true, viewerIsOwner: true);
        await form.ReadAsync(TestContext.Current.CancellationToken);
        (form.Username, form.Email, form.Password) = ("ana", "ana@example.com", "margin fossil");
        return form;
    }

    private JsonElement Sent() => JsonDocument.Parse(_silo.To("api/v2/admin/users").Single(r => r.Method == HttpMethod.Post).Body!).RootElement;

    [Fact]
    public async Task UntouchedFields_AreLeftOut_AndInherit()
    {
        var form = await StartedAsync();

        Assert.True(await form.SaveAsync(TestContext.Current.CancellationToken));

        var body = Sent();
        foreach (string field in new[] { "library_ids", "max_playback_quality", "max_streams", "max_transcodes", "max_remote_stream_bitrate_kbps",
            "max_local_stream_bitrate_kbps", "max_profiles", "transcode_allowed", "audio_transcode_allowed", "download_allowed",
            "download_transcode_allowed", "requests_allowed" })
        {
            Assert.False(body.TryGetProperty(field, out _), field);
        }
    }

    [Fact]
    public async Task Overrides_AreSent_ZeroAnyAndNoLibrariesIncluded()
    {
        var form = await StartedAsync();
        form.Policy.Downloads.SelectedIndex = 1;
        form.Policy.Requests.SelectedIndex = 2;
        form.Policy.MaxStreams.IsOverridden = true;
        form.Policy.MaxStreams.Draft = "0";
        form.Policy.QualityIndex = 1;
        form.Policy.InheritLibraries = false;
        foreach (var library in form.Policy.Libraries)
        {
            library.IsChecked = false;
        }
        form.Policy.MaxLocalBitrate.IsOverridden = true;
        form.Policy.MaxLocalBitrate.SelectedIndex = form.Policy.MaxLocalBitrate.Options.Count - 1;
        form.Policy.MaxLocalBitrate.CustomDraft = "1,5";

        Assert.True(await form.SaveAsync(TestContext.Current.CancellationToken));

        var body = Sent();
        Assert.True(body.GetProperty("download_allowed").GetBoolean());
        Assert.False(body.GetProperty("requests_allowed").GetBoolean());
        Assert.Equal(0, body.GetProperty("max_streams").GetInt32());
        Assert.Equal("", body.GetProperty("max_playback_quality").GetString());
        Assert.Equal(0, body.GetProperty("library_ids").GetArrayLength());
        Assert.Equal(1500, body.GetProperty("max_local_stream_bitrate_kbps").GetInt32());
        Assert.False(body.TryGetProperty("max_transcodes", out _));
    }

    [Fact]
    public async Task OverridingALimit_StartsFromWhatItInherits()
    {
        var form = await StartedAsync();
        form.SelectedGroupChoice = form.GroupChoices.Single(choice => choice.Value == "11");

        form.Policy.MaxStreams.IsOverridden = true;
        form.Policy.MaxRemoteBitrate.IsOverridden = true;

        Assert.Equal("2", form.Policy.MaxStreams.Draft);
        Assert.Equal(0, form.Policy.MaxRemoteBitrate.SelectedIndex);
        Assert.True(await form.SaveAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, Sent().GetProperty("max_streams").GetInt32());
        Assert.Equal(0, Sent().GetProperty("max_remote_stream_bitrate_kbps").GetInt32());
    }

    [Fact]
    public async Task AClearedLimit_ShowsTheLimitsTab_AndSendsNothing()
    {
        var form = await StartedAsync();
        form.Policy.MaxProfiles.IsOverridden = true;
        form.Policy.MaxProfiles.Draft = "0";

        Assert.False(await form.SaveAsync(TestContext.Current.CancellationToken));

        Assert.Equal(AdminNewUserViewModel.LimitsTab, form.SelectedTab);
        Assert.True(form.Policy.MaxProfiles.HasHelp);
        Assert.DoesNotContain(_silo.To("api/v2/admin/users"), request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task TheDefaults_FollowTheGroupAndRolePicked()
    {
        var form = await StartedAsync();

        Assert.Equal("Inherited: Unlimited", form.Policy.MaxStreams.DefaultText);
        Assert.Equal("Inherited: 8 Mbps", form.Policy.MaxRemoteBitrate.DefaultText);
        Assert.Equal("Inherited: Not allowed", form.Policy.Downloads.Options[0].Text);
        Assert.Equal("Inherited: 2 libraries", form.Policy.LibraryHint);
        Assert.Equal("Inherit from group", form.Policy.InheritLibrariesText);

        form.SelectedGroupChoice = form.GroupChoices.Single(choice => choice.Value == "11");
        Assert.Equal("Inherited: 2", form.Policy.MaxStreams.DefaultText);

        form.IsAdminRole = true;
        Assert.Equal("Admin default: Unlimited", form.Policy.MaxStreams.DefaultText);
        Assert.Equal("Admin default: Allowed", form.Policy.Downloads.Options[0].Text);
        Assert.Equal("Admin default: All libraries", form.Policy.LibraryHint);
    }

    [Fact]
    public async Task TheLibraries_AreTheServersInOrder_WithTheConversionNotice()
    {
        var form = await StartedAsync();

        Assert.Equal(["Movies", "Shows"], form.Policy.Libraries.Select(library => library.Name));
        Assert.True(form.Policy.Libraries[1].IsDisabled);
        Assert.Equal("web.admin.common.downloadAccess.conversionOff", form.Policy.DownloadsNoticeKey);
    }

    [Fact]
    public async Task OnlyTheOwner_MakesAnAdmin()
    {
        var form = new AdminNewUserViewModel(new AdminUsersApi(_silo.Http), new AdminApi(_silo.Http), _silo.Logs.For<AdminNewUserViewModel>());
        form.Start(AdminActors.Of(_session), new AdminUserCapabilities { Available = true, DefaultProfile = true }, Groups, groupsKnown: true, viewerIsOwner: false);
        await form.ReadAsync(TestContext.Current.CancellationToken);

        form.RoleIndex = 1;

        Assert.False(form.IsAdminRole);
    }

    [Fact]
    public void ABitrateTyped_IsReadAsTheWebReadsIt()
    {
        Assert.Equal(1500, AdminPolicyBitrateField.ParseMbps("1.5"));
        Assert.Equal(250, AdminPolicyBitrateField.ParseMbps(",25"));
        Assert.Null(AdminPolicyBitrateField.ParseMbps("0"));
        Assert.Null(AdminPolicyBitrateField.ParseMbps("1.2345"));
        Assert.Null(AdminPolicyBitrateField.ParseMbps("abc"));
    }
}
