using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.Profiles;

namespace Granary.Tests.Profiles;

/// <summary>The household's writes, as the web's profile editor sends them (docs/api-contract.md, 6.7).</summary>
public sealed class ProfilesApiTests
{
    private const string ProfilesPath = "api/v2/profiles";

    private const string ElenaJson = """
        {"id": "p-elena", "name": "Elena", "is_primary": false, "has_pin": false, "is_child": true,
         "avatar": "preset:dicebear:identicon:cosmic-otter", "avatar_source": "preset",
         "avatar_url": "https://api.dicebear.com/9.x/identicon/svg?seed=cosmic-otter",
         "language": "", "subtitle_language": "", "subtitle_mode": "", "max_content_rating": "PG", "max_advisory_age": 9,
         "require_advisory_age": true, "requests_allowed": false, "library_restrictions_enabled": true,
         "allowed_library_ids": ["2", "5"], "max_playback_quality": "1080p"}
        """;

    private readonly ScriptedSilo _silo = new();

    private ProfilesApi Api => new(_silo.Http);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheList_CarriesWhatTheServerLetsAProfileHave()
    {
        _silo.On(HttpMethod.Get, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.OK, $$"""
            {"items": [{{ElenaJson}}], "avatar_upload_enabled": true, "max_advisory_age_supported": true,
             "require_advisory_age_supported": true, "requests_allowed_supported": true, "max_profiles": 5}
            """));

        var list = await Api.ListAsync(ScriptedSilo.Base, Ct);

        Assert.True(list.MaxAdvisoryAgeSupported);
        Assert.True(list.RequireAdvisoryAgeSupported);
        Assert.True(list.RequestsAllowedSupported);
        Assert.Equal(5, list.MaxProfiles);
        Assert.True(list.HasRoom);
        var elena = Assert.Single(list.Items);
        Assert.Equal(("PG", 9, true, false, true), (elena.MaxContentRating, elena.MaxAdvisoryAge, elena.RequireAdvisoryAge, elena.MayRequest,
            elena.LibraryRestrictionsEnabled));
        Assert.Equal(["2", "5"], elena.AllowedLibraryIds);
        Assert.Equal("1080p", elena.MaxPlaybackQuality);
    }

    [Fact]
    public async Task AnOlderServer_ReportsNoLimit_AndItsProfilesMayRequest()
    {
        _silo.On(HttpMethod.Get, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.OK, """
            {"items": [{"id": "p-1", "name": "Elena", "is_primary": true, "has_pin": false, "is_child": false, "avatar": "",
              "avatar_source": "none", "language": "", "subtitle_language": "", "subtitle_mode": ""}], "avatar_upload_enabled": false}
            """));

        var list = await Api.ListAsync(ScriptedSilo.Base, Ct);

        Assert.Null(list.MaxProfiles);
        Assert.True(list.HasRoom);
        Assert.False(list.MaxAdvisoryAgeSupported);
        var elena = Assert.Single(list.Items);
        Assert.True(elena.MayRequest);
        Assert.Null(elena.MaxAdvisoryAge);
    }

    [Fact]
    public void AFullHousehold_HasNoRoom()
    {
        var list = new ProfileCollection([Primary()], false) { MaxProfiles = 1 };

        Assert.False(list.HasRoom);
    }

    [Fact]
    public async Task Create_SendsOnlyWhatIsSet_AsTheActingProfile()
    {
        _silo.On(HttpMethod.Post, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.Created, ElenaJson));

        var created = await Api.CreateAsync(ScriptedSilo.Base, new ProfileCreate("Elena")
        {
            Avatar = "preset:dicebear:identicon:cosmic-otter",
            Pin = "2580",
            IsChild = true,
            MaxContentRating = "PG",
            RequestsAllowed = false,
            LibraryRestrictionsEnabled = true,
            AllowedLibraryIds = ["2", "5"],
        }, ManagingProfile.Acting("p-owner"), Ct);

        Assert.Equal("p-elena", created.Id);
        var sent = Assert.Single(_silo.Sent);
        Assert.Equal((SiloAuth.Profile, "p-owner"), (sent.Auth, sent.AsProfile));
        Assert.Null(sent.Lent);
        Assert.Equal("""
            {"name":"Elena","avatar":"preset:dicebear:identicon:cosmic-otter","pin":"2580","is_child":true,"max_content_rating":"PG","requests_allowed":false,"library_restrictions_enabled":true,"allowed_library_ids":["2","5"]}
            """, sent.Body);
    }

    [Fact]
    public async Task Create_AsALentProfile_GoesWithTheAccountsCredentials_AndThatProfilesProof()
    {
        _silo.On(HttpMethod.Post, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.Created, ElenaJson));

        await Api.CreateAsync(ScriptedSilo.Base, new ProfileCreate("Elena"), ManagingProfile.Lent("p-owner", "pvt_owner"), Ct);

        var sent = Assert.Single(_silo.Sent);
        Assert.Equal(SiloAuth.Account, sent.Auth);
        Assert.Null(sent.AsProfile);
        Assert.Equal(("p-owner", "pvt_owner"), (sent.Lent?.ProfileId, sent.Lent?.Token));
        Assert.Equal("""{"name":"Elena","is_child":false,"library_restrictions_enabled":false,"allowed_library_ids":[]}""", sent.Body);
    }

    [Fact]
    public async Task Update_SendsEveryMemberTheEditorOwns_AClearedOneAsNull_AndLeavesThePinAlone()
    {
        _silo.On(HttpMethod.Patch, ProfilesPath + "/p-elena", ScriptedSilo.Json(HttpStatusCode.OK, ElenaJson));

        await Api.UpdateAsync(ScriptedSilo.Base, "p-elena", new ProfileUpdate("Elena") { Avatar = Change<string>.Clear },
            ManagingProfile.Acting("p-owner"), Ct);

        var sent = Assert.Single(_silo.Sent);
        Assert.Equal(HttpMethod.Patch, sent.Method);
        Assert.Equal("p-owner", sent.AsProfile);
        Assert.Equal("""
            {"name":"Elena","avatar":null,"is_child":false,"max_content_rating":null,"max_playback_quality":null,"library_restrictions_enabled":false,"allowed_library_ids":[]}
            """, sent.Body);
    }

    [Fact]
    public async Task Update_SetsAndClearsTheLimitsTheServerReports()
    {
        _silo.On(HttpMethod.Patch, ProfilesPath + "/p-elena", ScriptedSilo.Json(HttpStatusCode.OK, ElenaJson));

        await Api.UpdateAsync(ScriptedSilo.Base, "p-elena", new ProfileUpdate("Elena")
        {
            Avatar = Change<string>.To("preset:avatar-3"),
            Pin = Change<string>.To("1234"),
            IsChild = true,
            MaxContentRating = "PG",
            MaxAdvisoryAge = Change<int>.To(9),
            RequireAdvisoryAge = true,
            RequestsAllowed = false,
            MaxPlaybackQuality = "1080p",
            LibraryRestrictionsEnabled = true,
            AllowedLibraryIds = ["2"],
        }, ManagingProfile.Acting("p-owner"), Ct);
        await Api.UpdateAsync(ScriptedSilo.Base, "p-elena", new ProfileUpdate("Elena")
        {
            Pin = Change<string>.Clear,
            MaxAdvisoryAge = Change<int>.Clear,
            RequireAdvisoryAge = false,
            RequestsAllowed = true,
        }, ManagingProfile.Acting("p-owner"), Ct);

        Assert.Equal(
            [
                """{"name":"Elena","avatar":"preset:avatar-3","pin":"1234","is_child":true,"max_content_rating":"PG","max_advisory_age":9,"require_advisory_age":true,"requests_allowed":false,"max_playback_quality":"1080p","library_restrictions_enabled":true,"allowed_library_ids":["2"]}""",
                """{"name":"Elena","pin":null,"is_child":false,"max_content_rating":null,"max_advisory_age":null,"require_advisory_age":false,"requests_allowed":true,"max_playback_quality":null,"library_restrictions_enabled":false,"allowed_library_ids":[]}""",
            ],
            _silo.Sent.Select(r => r.Body));
    }

    [Fact]
    public void APin_NeverShowsInWhatAWriteSaysAboutItself()
    {
        string create = new ProfileCreate("Elena") { Pin = "2580" }.ToString();
        string update = new ProfileUpdate("Elena") { Pin = Change<string>.To("2580") }.ToString();
        string lent = ManagingProfile.Lent("p-owner", "pvt_secret").ToString();

        Assert.DoesNotContain("2580", create, StringComparison.Ordinal);
        Assert.DoesNotContain("2580", update, StringComparison.Ordinal);
        Assert.DoesNotContain("pvt_secret", lent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("p-owner\r\nX-Evil: 1", null)]
    [InlineData("p-owner", "pvt with space")]
    public void AProfileOrProofThatCannotBeSentBack_IsNotLent(string profileId, string? token)
    {
        var error = Assert.Throws<HttpRequestException>(() => ManagingProfile.Lent(profileId, token));

        Assert.Equal(HttpRequestError.InvalidResponse, error.HttpRequestError);
    }

    [Fact]
    public async Task Delete_AndTheUploadsDelete_AreBodilessDeletes()
    {
        _silo.On(HttpMethod.Delete, ProfilesPath + "/p-elena", ScriptedSilo.NoContent());
        _silo.On(HttpMethod.Delete, ProfilesPath + "/p-elena/avatar", ScriptedSilo.NoContent());

        await Api.DeleteAsync(ScriptedSilo.Base, "p-elena", ManagingProfile.Acting("p-owner"), Ct);
        await Api.DeleteAvatarAsync(ScriptedSilo.Base, "p-elena", ManagingProfile.Acting("p-owner"), Ct);

        Assert.Equal(
            ["https://nas.example/silo/api/v2/profiles/p-elena", "https://nas.example/silo/api/v2/profiles/p-elena/avatar"],
            _silo.Sent.Select(r => r.Uri.AbsoluteUri));
        Assert.All(_silo.Sent, r => Assert.Equal((HttpMethod.Delete, "p-owner", (string?)null), (r.Method, r.AsProfile, r.Body)));
    }

    [Fact]
    public async Task APicture_IsUploadedAsTheFormsAvatarPart()
    {
        _silo.On(HttpMethod.Put, ProfilesPath + "/p-elena/avatar", ScriptedSilo.Json(HttpStatusCode.OK, ElenaJson));

        await Api.UploadAvatarAsync(ScriptedSilo.Base, "p-elena", new byte[] { 1, 2, 3 }, "elena.png", "image/png",
            ManagingProfile.Lent("p-owner", null), Ct);

        var sent = Assert.Single(_silo.Sent);
        Assert.Equal(HttpMethod.Put, sent.Method);
        Assert.Equal("multipart/form-data", sent.ContentType);
        Assert.Contains("name=avatar", sent.Body, StringComparison.Ordinal);
        Assert.Contains("filename=elena.png", sent.Body, StringComparison.Ordinal);
        Assert.Contains("Content-Type: image/png", sent.Body, StringComparison.Ordinal);
        Assert.Equal(("p-owner", (string?)null), (sent.Lent?.ProfileId, sent.Lent?.Token));
    }

    [Fact]
    public async Task TheLibrariesAProfileCanBeLimitedTo_IncludeTheOnesTheManagerHid()
    {
        _silo.On(HttpMethod.Get, "api/v2/user/libraries", ScriptedSilo.Json(HttpStatusCode.OK, """
            {"items": [{"id": "2", "name": "Movies", "type": "movies", "sort_order": 1}], "page": null}
            """));

        var libraries = await Api.ListLibrariesAsync(ScriptedSilo.Base, ManagingProfile.Acting("p-owner"), Ct);

        Assert.Equal("Movies", Assert.Single(libraries.Items).Name);
        var sent = Assert.Single(_silo.Sent);
        Assert.Equal("?include_hidden=true", sent.Uri.Query);
        Assert.Equal("p-owner", sent.AsProfile);
    }

    private static Profile Primary() => new("p-owner", "Elena Moreau", true, false, false, "", "none", null, "", "", "");
}
