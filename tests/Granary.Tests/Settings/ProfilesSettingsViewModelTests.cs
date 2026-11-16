using System.Net;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Settings;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Settings;

/// <summary>Settings → Profiles, as the web's ProfilesSettings: the household, its badges and limits, and Delete.</summary>
public sealed class ProfilesSettingsViewModelTests
{
    internal const string ProfilesPath = "api/v2/profiles";

    internal const string HouseholdJson = """
        {"items": [
          {"id": "p-owner", "name": "Elena Moreau", "is_primary": true, "has_pin": true, "is_child": false, "avatar": "",
           "avatar_source": "none", "language": "", "subtitle_language": "", "subtitle_mode": "", "max_content_rating": "",
           "library_restrictions_enabled": false, "allowed_library_ids": [], "max_playback_quality": ""},
          {"id": "p-kid", "name": "Mila", "is_primary": false, "has_pin": false, "is_child": true, "avatar": "preset:avatar-3",
           "avatar_source": "preset", "avatar_url": "/profile-avatars/avatar-3.svg", "language": "", "subtitle_language": "",
           "subtitle_mode": "", "max_content_rating": "PG", "max_advisory_age": 9, "require_advisory_age": false,
           "requests_allowed": false, "library_restrictions_enabled": true, "allowed_library_ids": ["2", "2", "5"],
           "max_playback_quality": "1080p"}
        ],
        "avatar_upload_enabled": true, "max_advisory_age_supported": true, "require_advisory_age_supported": true,
        "requests_allowed_supported": true, "max_profiles": 5}
        """;

    private readonly ScriptedSilo _silo = new();
    private readonly FakeConfirmation _confirmation = new();
    private readonly FakeSessionManager _session = new()
    {
        State = SessionState.Ready,
        ActiveServer = ScriptedSilo.Server,
        ProfileId = "p-owner",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProfilesSettingsViewModel Create() =>
        new(new ProfilesApi(_silo.Http), _session, _confirmation, new RecordingLogger<ProfilesSettingsViewModel>());

    [Fact]
    public async Task TheHousehold_ShowsEachProfilesBadgesAndLimits()
    {
        _silo.On(HttpMethod.Get, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.OK, HouseholdJson));
        var page = Create();

        await page.LoadAsync(Ct);

        Assert.Equal(["Elena Moreau", "Mila"], page.Profiles.Select(row => row.Name));
        var owner = page.Profiles[0];
        Assert.Equal((true, true, false, true), (owner.IsCurrent, owner.IsPrimary, owner.IsKids, owner.HasPin));
        Assert.Equal("Any content · All libraries · Any quality", owner.AccessSummary);
        Assert.Null(owner.Avatar);
        Assert.Equal("E", owner.Initial);
        var kid = page.Profiles[1];
        Assert.Equal((false, false, true, false), (kid.IsCurrent, kid.IsPrimary, kid.IsKids, kid.HasPin));
        Assert.Equal("PG max · Advisory age 9 max · 2 libraries · Standard quality · No requests", kid.AccessSummary);
        Assert.Equal(new Uri("https://nas.example/silo/profile-avatars/avatar-3.svg"), kid.Avatar?.Url);
        Assert.True(kid.Avatar?.IsServerPreset);
        Assert.True(page.CanAdd);
        Assert.Equal("", page.LimitText);
        Assert.False(page.IsEmpty);
        Assert.Equal(Granary.Core.Api.SiloAuth.Account, Assert.Single(_silo.Sent).Auth);
    }

    [Fact]
    public async Task Delete_IsHeldBack_ForTheLastThePrimaryAndTheActingProfile()
    {
        _silo.On(HttpMethod.Get, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.OK, HouseholdJson));
        var page = Create();

        await page.LoadAsync(Ct);

        Assert.Equal("The primary profile can only be removed by deleting the account.", page.Profiles[0].DeleteGuard);
        Assert.False(page.Profiles[0].CanDelete);
        Assert.True(page.Profiles[1].CanDelete);
        var alone = new Profile("p-1", "Elena", false, false, false, "", "none", null, "", "", "");
        Assert.Equal("At least one profile is required.", ProfileSettingsRow.GuardOf(alone, 1, "p-9"));
        Assert.Equal("Switch to another profile before deleting this one.", ProfileSettingsRow.GuardOf(alone, 2, "p-1"));
    }

    [Fact]
    public async Task AFullHousehold_OffersNoNewProfile_AndSaysItsLimit()
    {
        _silo.On(HttpMethod.Get, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.OK, HouseholdJson.Replace("\"max_profiles\": 5", "\"max_profiles\": 2",
            StringComparison.Ordinal)));
        var page = Create();

        await page.LoadAsync(Ct);

        Assert.False(page.CanAdd);
        Assert.Equal("This account can have 2 profiles. An administrator can raise the limit.", page.LimitText);
    }

    [Fact]
    public async Task Delete_AsksAsTheWebDoes_ThenDeletesAsTheActingProfile_AndReadsTheHouseholdAgain()
    {
        _silo.On(HttpMethod.Get, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.OK, HouseholdJson));
        _silo.On(HttpMethod.Delete, ProfilesPath + "/p-kid", ScriptedSilo.NoContent());
        var page = Create();
        await page.LoadAsync(Ct);

        await page.DeleteCommand.ExecuteAsync(page.Profiles[1]);

        var asked = Assert.Single(_confirmation.Asked);
        Assert.Equal(("Delete profile", "Delete profile \"Mila\"? This action cannot be undone.", "Delete"), asked);
        var delete = Assert.Single(_silo.SentTo(HttpMethod.Delete, ProfilesPath + "/p-kid"));
        Assert.Equal("p-owner", delete.AsProfile);
        Assert.Equal(2, _silo.SentTo(HttpMethod.Get, ProfilesPath).Count);
        Assert.Equal("Profile deleted", page.Status);
    }

    [Fact]
    public async Task Delete_Cancelled_DeletesNothing()
    {
        _silo.On(HttpMethod.Get, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.OK, HouseholdJson));
        _confirmation.Answer = false;
        var page = Create();
        await page.LoadAsync(Ct);

        await page.DeleteCommand.ExecuteAsync(page.Profiles[1]);

        Assert.Empty(_silo.SentTo(HttpMethod.Delete, ProfilesPath + "/p-kid"));
        Assert.Equal("", page.Status);
    }

    [Fact]
    public async Task Delete_OfAHeldBackProfile_AsksNothing()
    {
        _silo.On(HttpMethod.Get, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.OK, HouseholdJson));
        var page = Create();
        await page.LoadAsync(Ct);

        await page.DeleteCommand.ExecuteAsync(page.Profiles[0]);

        Assert.Empty(_confirmation.Asked);
    }

    [Fact]
    public async Task Delete_ThatFailed_KeepsTheProfile_AndSaysSo()
    {
        _silo.On(HttpMethod.Get, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.OK, HouseholdJson));
        _silo.On(HttpMethod.Delete, ProfilesPath + "/p-kid", ScriptedSilo.Problem(500, "internal_error"));
        var page = Create();
        await page.LoadAsync(Ct);

        await page.DeleteCommand.ExecuteAsync(page.Profiles[1]);

        Assert.Equal(["Elena Moreau", "Mila"], page.Profiles.Select(row => row.Name));
        Assert.Equal("Failed to delete", page.Status);
        Assert.False(page.Profiles[1].IsBusy);
    }

    [Fact]
    public async Task Delete_OfAProfileGoneAlready_ReadsTheHouseholdAgain()
    {
        _silo.On(HttpMethod.Get, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.OK, HouseholdJson));
        _silo.On(HttpMethod.Delete, ProfilesPath + "/p-kid", ScriptedSilo.Problem(404, "not_found"));
        var page = Create();
        await page.LoadAsync(Ct);

        await page.DeleteCommand.ExecuteAsync(page.Profiles[1]);

        Assert.Equal("Profile deleted", page.Status);
        Assert.Equal(2, _silo.SentTo(HttpMethod.Get, ProfilesPath).Count);
    }

    [Fact]
    public async Task Delete_AfterAnotherProfileStartedActing_SendsNothing()
    {
        _silo.On(HttpMethod.Get, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.OK, HouseholdJson));
        var page = Create();
        await page.LoadAsync(Ct);
        _confirmation.WhileAsking = () =>
        {
            _session.ProfileId = "p-kid";
            return Task.CompletedTask;
        };

        await page.DeleteCommand.ExecuteAsync(page.Profiles[1]);

        Assert.Empty(_silo.SentTo(HttpMethod.Delete, ProfilesPath + "/p-kid"));
    }

    [Fact]
    public async Task AListThatCouldNotBeRead_SaysSo_AndOffersRetry()
    {
        _silo.On(HttpMethod.Get, ProfilesPath, ScriptedSilo.NetworkError(), ScriptedSilo.Json(HttpStatusCode.OK, HouseholdJson));
        var page = Create();

        await page.LoadAsync(Ct);

        Assert.True(page.HasLoadError);
        Assert.NotEqual("", page.Status);
        Assert.False(page.IsLoaded);

        await page.RefreshCommand.ExecuteAsync(null);

        Assert.False(page.HasLoadError);
        Assert.Equal("", page.Status);
        Assert.Equal(2, page.Profiles.Count);
    }

    [Fact]
    public async Task AnAccountWithoutProfiles_ShowsTheEmptyList()
    {
        _silo.On(HttpMethod.Get, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.OK, """{"items": [], "avatar_upload_enabled": false}"""));
        var page = Create();

        await page.LoadAsync(Ct);

        Assert.True(page.IsEmpty);
        Assert.True(page.CanAdd);
    }
}
