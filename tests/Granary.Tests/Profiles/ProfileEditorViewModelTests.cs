using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.Profiles;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Profiles;
using Granary.Tests.Fakes;

namespace Granary.Tests.Profiles;

/// <summary>The profile editor, as the web's ProfileEditorDialog and lib/profile-management.ts (docs/api-contract.md, 6.7).</summary>
public sealed class ProfileEditorViewModelTests
{
    private const string ProfilesPath = "api/v2/profiles";

    private const string LibrariesJson = """
        {"items": [{"id": "5", "name": "Shows", "type": "series", "sort_order": 2}, {"id": "2", "name": "Movies", "type": "movies", "sort_order": 1}], "page": null}
        """;

    private static readonly Profile Owner = new("p-owner", "Elena Moreau", true, true, false, "", "none", null, "", "", "");

    private static readonly Profile Kid = new("p-kid", "Mila", false, false, true, "preset:dicebear:identicon:cosmic-otter", "preset",
        "https://api.dicebear.com/9.x/identicon/svg?seed=cosmic-otter", "", "", "")
    {
        MaxContentRating = "PG",
        MaxAdvisoryAge = 9,
        RequestsAllowed = false,
        LibraryRestrictionsEnabled = true,
        AllowedLibraryIds = ["2"],
        MaxPlaybackQuality = "1080p",
    };

    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    private readonly ScriptedSilo _silo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProfileCollection Household(bool flags = true, int? maxProfiles = 5, bool uploads = true, params Profile[] profiles) =>
        new(profiles.Length == 0 ? [Owner, Kid] : profiles, uploads)
        {
            MaxAdvisoryAgeSupported = flags,
            RequireAdvisoryAgeSupported = flags,
            RequestsAllowedSupported = flags,
            MaxProfiles = maxProfiles,
        };

    private ProfileEditorViewModel Editor(Profile? profile = null, ProfileCollection? household = null) =>
        new(new ProfilesApi(_silo.Http), new ProfileEditorTarget(ScriptedSilo.Base, ManagingProfile.Acting("1", "p-owner"), household ?? Household()),
            profile, new RecordingLogger<ProfileEditorViewModel>());

    private void ScriptLibraries() => _silo.On(HttpMethod.Get, "api/v2/user/libraries", ScriptedSilo.Json(HttpStatusCode.OK, LibrariesJson));

    [Fact]
    public async Task ANewKidsProfile_StartsSafer_AndIsSentAsTheWebSendsIt()
    {
        ScriptLibraries();
        _silo.On(HttpMethod.Post, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.Created, """{"id":"p-new","name":"Leo","is_primary":false,"has_pin":true,"is_child":true}"""));
        var editor = Editor();
        await editor.LoadLibrariesAsync(Ct);
        editor.Name = "  Leo ";
        editor.Pin = "12a34";

        editor.IsChild = true;
        editor.Libraries.Single(l => l.Name == "Movies").IsChecked = true;
        var saved = await editor.SaveAsync(Ct);

        Assert.Equal(["Movies", "Shows"], editor.Libraries.Select(l => l.Name));
        Assert.Equal(("PG", false, true), (editor.SelectedContentRating.Value, editor.RequestsAllowed, editor.LibraryRestrictionsEnabled));
        Assert.Equal((true, "1234"), (saved?.Created, saved?.NewPin));
        var sent = Assert.Single(_silo.SentTo(HttpMethod.Post, ProfilesPath));
        Assert.Equal(("p-owner", "1"), (sent.AsProfile, sent.AsAccount));
        Assert.Equal("""
            {"name":"Leo","pin":"1234","is_child":true,"max_content_rating":"PG","requests_allowed":false,"library_restrictions_enabled":true,"allowed_library_ids":["2"]}
            """, sent.Body);
    }

    [Fact]
    public void LeavingKids_TakesEveryLimitAway()
    {
        var editor = Editor(Kid);

        editor.IsChild = false;

        Assert.Equal(("", (int?)null, PlaybackCeiling.Any, false, true), (editor.SelectedContentRating.Value, editor.SelectedAdvisoryAge.Value,
            editor.SelectedPlaybackCeiling.Value, editor.LibraryRestrictionsEnabled, editor.RequestsAllowed));
    }

    [Fact]
    public void Kids_LeavesWhatWasSetByHand()
    {
        var editor = Editor();
        editor.SelectedContentRating = editor.ContentRatings.Single(c => c.Value == "12");
        editor.RequestsAllowed = false;
        editor.RequestsAllowed = true;
        editor.LibraryRestrictionsEnabled = true;
        editor.LibraryRestrictionsEnabled = false;

        editor.IsChild = true;

        Assert.Equal(("12", true, false), (editor.SelectedContentRating.Value, editor.RequestsAllowed, editor.LibraryRestrictionsEnabled));
    }

    [Fact]
    public async Task ThePrimaryProfile_TakesNoLibraryLimit_AndClearsOneAnOlderServerStored()
    {
        _silo.On(HttpMethod.Patch, ProfilesPath + "/p-owner", ScriptedSilo.Json(HttpStatusCode.OK, """{"id":"p-owner","name":"Elena Moreau","is_primary":true,"has_pin":true,"is_child":false}"""));
        var editor = Editor(Owner with { LibraryRestrictionsEnabled = true, AllowedLibraryIds = ["2"] });

        await editor.LoadLibrariesAsync(Ct);
        editor.IsChild = true;
        await editor.SaveAsync(Ct);

        Assert.False(editor.CanLimitLibraries);
        Assert.Empty(_silo.SentTo(HttpMethod.Get, "api/v2/user/libraries"));
        Assert.Contains("\"library_restrictions_enabled\":false,\"allowed_library_ids\":[]", Assert.Single(_silo.Sent).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEditOfAnUnchangedProfile_SendsWhatItHas_AndLeavesThePinAlone()
    {
        ScriptLibraries();
        _silo.On(HttpMethod.Patch, ProfilesPath + "/p-kid", ScriptedSilo.Json(HttpStatusCode.OK, """{"id":"p-kid","name":"Mila","is_primary":false,"has_pin":false,"is_child":true}"""));
        var editor = Editor(Kid);
        await editor.LoadLibrariesAsync(Ct);

        var saved = await editor.SaveAsync(Ct);

        Assert.Equal((false, "", false), (saved?.Created, saved?.NewPin, saved?.PinCleared));
        Assert.Equal("""
            {"name":"Mila","avatar":"preset:dicebear:identicon:cosmic-otter","is_child":true,"max_content_rating":"PG","max_advisory_age":9,"require_advisory_age":false,"requests_allowed":false,"max_playback_quality":"1080p","library_restrictions_enabled":true,"allowed_library_ids":["2"]}
            """, Assert.Single(_silo.SentTo(HttpMethod.Patch, ProfilesPath + "/p-kid")).Body);
    }

    [Fact]
    public async Task AServerWithoutTheNewerLimits_GetsNoneOfTheirMembers()
    {
        _silo.On(HttpMethod.Post, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.Created, """{"id":"p-new","name":"Leo","is_primary":false,"has_pin":false,"is_child":false}"""));
        var editor = Editor(household: Household(flags: false));
        editor.Name = "Leo";
        editor.SelectedAdvisoryAge = editor.AdvisoryAges.Single(a => a.Value == 12);
        editor.RequestsAllowed = false;

        await editor.SaveAsync(Ct);

        Assert.False(editor.ShowsAdvisoryAge || editor.ShowsRequests || editor.ShowsRequireAdvisoryAge);
        Assert.Equal("""{"name":"Leo","is_child":false,"library_restrictions_enabled":false,"allowed_library_ids":[]}""", Assert.Single(_silo.Sent).Body);
    }

    [Fact]
    public async Task AnEmptyOrTakenName_AndAShortPin_AreSaidBeforeAnythingIsSent()
    {
        var editor = Editor();

        Assert.Null(await editor.SaveAsync(Ct));
        Assert.Equal("Enter a profile name.", editor.NameError);

        editor.Name = " mila ";
        editor.Pin = "12";
        Assert.Null(await editor.SaveAsync(Ct));

        Assert.Equal("A profile with this name already exists", editor.NameError);
        Assert.Equal("PIN must be exactly 4 digits.", editor.PinError);
        Assert.Empty(_silo.Sent);
    }

    [Fact]
    public async Task AProfileKeepsItsOwnName()
    {
        ScriptLibraries();
        _silo.On(HttpMethod.Patch, ProfilesPath + "/p-kid", ScriptedSilo.Json(HttpStatusCode.OK, """{"id":"p-kid","name":"MILA","is_primary":false,"has_pin":false,"is_child":true}"""));
        var editor = Editor(Kid);
        await editor.LoadLibrariesAsync(Ct);
        editor.Name = "MILA";

        Assert.NotNull(await editor.SaveAsync(Ct));
    }

    [Fact]
    public async Task AFullHousehold_SaysItsLimit_AndCreatesNothing()
    {
        var editor = Editor(household: Household(maxProfiles: 2));
        editor.Name = "Leo";

        Assert.Null(await editor.SaveAsync(Ct));

        Assert.Equal("This account can have 2 profiles. An administrator can raise the limit.", editor.Status);
        Assert.Empty(_silo.Sent);
    }

    [Fact]
    public async Task LimitedLibraries_NeedOne()
    {
        ScriptLibraries();
        var editor = Editor();
        await editor.LoadLibrariesAsync(Ct);
        editor.Name = "Leo";
        editor.LibraryRestrictionsEnabled = true;

        Assert.Null(await editor.SaveAsync(Ct));

        Assert.Equal("Choose at least one library.", editor.LibrariesError);
        Assert.Empty(_silo.SentTo(HttpMethod.Post, ProfilesPath));
    }

    [Fact]
    public async Task RemovePin_SendsNull()
    {
        _silo.On(HttpMethod.Patch, ProfilesPath + "/p-owner", ScriptedSilo.Json(HttpStatusCode.OK, """{"id":"p-owner","name":"Elena Moreau","is_primary":true,"has_pin":false,"is_child":false}"""));
        var editor = Editor(Owner);

        editor.TogglePinRemoval();
        var saved = await editor.SaveAsync(Ct);

        Assert.True(saved?.PinCleared);
        Assert.Equal("PIN will be removed", editor.PinLabel);
        Assert.Contains("\"pin\":null", Assert.Single(_silo.Sent).Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(403, "permission_denied", "granary.loadError.denied")]
    [InlineData(409, "conflict", "web.common.toast.profiles.saveFailed")]
    public async Task ARefusedSave_IsWordedAsTheLibrariesAndTheWebWordIt(int status, string code, string key)
    {
        _silo.On(HttpMethod.Post, ProfilesPath, ScriptedSilo.Problem(status, code));
        var editor = Editor();
        editor.Name = "Leo";

        Assert.Null(await editor.SaveAsync(Ct));

        Assert.Equal(Core.Texts.AppText.Get(key), editor.Status);
        Assert.False(editor.IsSaving);
    }

    [Fact]
    public async Task ANewPicture_IsUploadedAfterTheSave_AsTheManager()
    {
        _silo.On(HttpMethod.Post, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.Created, """{"id":"p-new","name":"Leo","is_primary":false,"has_pin":false,"is_child":false}"""));
        _silo.On(HttpMethod.Put, ProfilesPath + "/p-new/avatar", ScriptedSilo.Json(HttpStatusCode.OK,
            """{"id":"p-new","name":"Leo","is_primary":false,"has_pin":false,"is_child":false,"avatar_source":"upload"}"""));
        var editor = Editor();
        editor.Name = "Leo";

        Assert.True(editor.ChoosePicture(Png, @"C:\Pictures\leo.png"));
        var saved = await editor.SaveAsync(Ct);

        Assert.Equal(("upload", ""), (saved?.Profile.AvatarSource, saved?.Notice));
        var upload = Assert.Single(_silo.SentTo(HttpMethod.Put, ProfilesPath + "/p-new/avatar"));
        Assert.Equal(("p-owner", "1"), (upload.AsProfile, upload.AsAccount));
        Assert.Contains("filename=leo.png", upload.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APictureThatFails_KeepsTheSave_AndSaysSo()
    {
        _silo.On(HttpMethod.Post, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.Created, """{"id":"p-new","name":"Leo","is_primary":false,"has_pin":false,"is_child":false}"""));
        _silo.On(HttpMethod.Put, ProfilesPath + "/p-new/avatar", ScriptedSilo.Problem(503, "dependency_unavailable"));
        var editor = Editor();
        editor.Name = "Leo";
        editor.ChoosePicture(Png, "leo.png");

        var saved = await editor.SaveAsync(Ct);

        Assert.Equal(("p-new", "Failed to upload avatar"), (saved?.Profile.Id, saved?.Notice));
    }

    [Fact]
    public void APictureTheServerWouldRefuse_IsNotKept()
    {
        var editor = Editor();

        Assert.False(editor.ChoosePicture("GIF89a"u8.ToArray(), "leo.gif"));

        Assert.False(editor.HasNewPicture);
        Assert.Equal("Use a JPEG, PNG or WebP image.", editor.PictureError);
        editor.RejectPicture(AvatarProblem.TooLarge);
        Assert.Equal("The avatar must be under 10 MB.", editor.PictureError);
    }

    [Fact]
    public async Task AnUploadedPictureThatStays_IsLeftOutOfTheUpdate()
    {
        _silo.On(HttpMethod.Patch, ProfilesPath + "/p-owner", ScriptedSilo.Json(HttpStatusCode.OK, """{"id":"p-owner","name":"Elena Moreau","is_primary":true,"has_pin":true,"is_child":false}"""));
        var editor = Editor(Owner with { Avatar = "upload:x", AvatarSource = "upload", AvatarUrl = "/avatars/p-owner.webp" });

        await editor.SaveAsync(Ct);

        Assert.True(editor.HasUploadedPicture);
        Assert.DoesNotContain("\"avatar\"", Assert.Single(_silo.Sent).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteImage_GoesThroughItsOwnCall_AfterTheUpdate()
    {
        _silo.On(HttpMethod.Patch, ProfilesPath + "/p-owner", ScriptedSilo.Json(HttpStatusCode.OK, """{"id":"p-owner","name":"Elena Moreau","is_primary":true,"has_pin":true,"is_child":false}"""));
        _silo.On(HttpMethod.Delete, ProfilesPath + "/p-owner/avatar", ScriptedSilo.NoContent());
        var editor = Editor(Owner with { Avatar = "upload:x", AvatarSource = "upload", AvatarUrl = "/avatars/p-owner.webp" });

        editor.DeleteUploadedPicture();
        await editor.SaveAsync(Ct);

        Assert.Equal([HttpMethod.Patch, HttpMethod.Delete], _silo.Sent.Select(r => r.Method));
        Assert.DoesNotContain("\"avatar\"", _silo.Sent[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APreset_ReplacesAnUploadedPicture()
    {
        _silo.On(HttpMethod.Patch, ProfilesPath + "/p-owner", ScriptedSilo.Json(HttpStatusCode.OK, """{"id":"p-owner","name":"Elena Moreau","is_primary":true,"has_pin":true,"is_child":false}"""));
        var editor = Editor(Owner with { Avatar = "upload:x", AvatarSource = "upload", AvatarUrl = "/avatars/p-owner.webp" });

        editor.SelectPreset(editor.Presets[0]);
        await editor.SaveAsync(Ct);

        Assert.Equal(ProfileAvatarPresets.BatchSize, editor.Presets.Count);
        Assert.StartsWith("https://api.dicebear.com/9.x/identicon/svg?seed=", editor.Preview?.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains($"\"avatar\":\"preset:{editor.Presets[0].Id}\"", Assert.Single(_silo.Sent).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewPinWithANewPicture_SendsThePictureFirst_WhileTheOldProofStillHolds()
    {
        _silo.On(HttpMethod.Put, ProfilesPath + "/p-owner/avatar", ScriptedSilo.Json(HttpStatusCode.OK,
            """{"id":"p-owner","name":"Elena Moreau","is_primary":true,"has_pin":true,"is_child":false,"avatar_source":"upload"}"""));
        _silo.On(HttpMethod.Patch, ProfilesPath + "/p-owner", ScriptedSilo.Json(HttpStatusCode.OK, """{"id":"p-owner","name":"Elena Moreau","is_primary":true,"has_pin":true,"is_child":false}"""));
        var editor = Editor(Owner);
        editor.SelectPreset(editor.Presets[0]);
        editor.ChoosePicture(Png, "elena.png");
        editor.Pin = "2580";

        var saved = await editor.SaveAsync(Ct);

        Assert.Equal([HttpMethod.Put, HttpMethod.Patch], _silo.Sent.Select(r => r.Method));
        Assert.DoesNotContain("\"avatar\"", _silo.Sent[1].Body, StringComparison.Ordinal);
        Assert.Equal("2580", saved?.NewPin);
        Assert.DoesNotContain("2580", saved!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePinSaidBack_IsTheOneSent_AndCannotBeTypedWhileTheSaveRuns()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.OnCancellable(HttpMethod.Patch, ProfilesPath + "/p-owner", _ => answer.Task);
        var editor = Editor(Owner);
        editor.Pin = "2580";

        var saving = editor.SaveAsync(Ct);
        Assert.False(editor.CanTypePin);
        editor.Pin = "1111";
        answer.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"p-owner","name":"Elena Moreau","is_primary":true,"has_pin":true,"is_child":false}""",
                System.Text.Encoding.UTF8, "application/json"),
        });
        var saved = await saving;

        Assert.Equal("2580", saved?.NewPin);
        Assert.Contains("\"pin\":\"2580\"", Assert.Single(_silo.Sent).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewImageForAnUploadedPicture_LeavesTheOldOneUntilTheUploadWent()
    {
        _silo.On(HttpMethod.Patch, ProfilesPath + "/p-owner", ScriptedSilo.Json(HttpStatusCode.OK, """{"id":"p-owner","name":"Elena Moreau","is_primary":true,"has_pin":true,"is_child":false}"""));
        _silo.On(HttpMethod.Put, ProfilesPath + "/p-owner/avatar", ScriptedSilo.Problem(503, "dependency_unavailable"));
        var editor = Editor(Owner with { Avatar = "upload:x", AvatarSource = "upload", AvatarUrl = "/avatars/p-owner.webp" });
        editor.ChoosePicture(Png, "elena.png");

        var saved = await editor.SaveAsync(Ct);

        Assert.DoesNotContain("\"avatar\"", _silo.SentTo(HttpMethod.Patch, ProfilesPath + "/p-owner").Single().Body, StringComparison.Ordinal);
        Assert.Equal("Failed to upload avatar", saved?.Notice);
    }

    [Fact]
    public async Task APictureSentFirst_ThatWentThrough_IsSaid_WhenTheUpdateFails_AndNotSentAgain()
    {
        _silo.On(HttpMethod.Put, ProfilesPath + "/p-owner/avatar", ScriptedSilo.Json(HttpStatusCode.OK,
            """{"id":"p-owner","name":"Elena Moreau","is_primary":true,"has_pin":true,"is_child":false,"avatar_source":"upload"}"""));
        _silo.On(HttpMethod.Patch, ProfilesPath + "/p-owner", ScriptedSilo.Problem(500, "internal_error"),
            ScriptedSilo.Json(HttpStatusCode.OK, """{"id":"p-owner","name":"Elena Moreau","is_primary":true,"has_pin":true,"is_child":false}"""));
        var editor = Editor(Owner);
        editor.ChoosePicture(Png, "elena.png");
        editor.Pin = "2580";

        Assert.Null(await editor.SaveAsync(Ct));
        Assert.Equal("Failed to save profile · Avatar updated", editor.Status);
        Assert.NotNull(await editor.SaveAsync(Ct));

        Assert.Single(_silo.SentTo(HttpMethod.Put, ProfilesPath + "/p-owner/avatar"));
        Assert.DoesNotContain("\"avatar\"", _silo.SentTo(HttpMethod.Patch, ProfilesPath + "/p-owner")[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LimitsThisClientDoesNotKnow_AreKeptByAnUnrelatedEdit()
    {
        _silo.On(HttpMethod.Patch, ProfilesPath + "/p-owner", ScriptedSilo.Json(HttpStatusCode.OK, """{"id":"p-owner","name":"Elena Moreau","is_primary":true,"has_pin":true,"is_child":false}"""));
        var editor = Editor(Owner with { MaxContentRating = "NC-17", MaxPlaybackQuality = "4320p" });

        editor.Name = "Elena";
        await editor.SaveAsync(Ct);

        string body = Assert.Single(_silo.Sent).Body!;
        Assert.Contains("\"max_content_rating\":\"NC-17\"", body, StringComparison.Ordinal);
        Assert.Contains("\"max_playback_quality\":\"4320p\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavingKids_ClearsAQualityThisClientDoesNotKnow()
    {
        ScriptLibraries();
        _silo.On(HttpMethod.Patch, ProfilesPath + "/p-kid", ScriptedSilo.Json(HttpStatusCode.OK, """{"id":"p-kid","name":"Mila","is_primary":false,"has_pin":false,"is_child":false}"""));
        var editor = Editor(Kid with { MaxPlaybackQuality = "4320p" });
        await editor.LoadLibrariesAsync(Ct);

        editor.IsChild = false;
        await editor.SaveAsync(Ct);

        Assert.Contains("\"max_playback_quality\":null", Assert.Single(_silo.SentTo(HttpMethod.Patch, ProfilesPath + "/p-kid")).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APictureSavedBefore_StaysWhenAnotherImageIsStagedAndRemoved()
    {
        _silo.On(HttpMethod.Put, ProfilesPath + "/p-owner/avatar", ScriptedSilo.Json(HttpStatusCode.OK,
            """{"id":"p-owner","name":"Elena Moreau","is_primary":true,"has_pin":true,"is_child":false,"avatar_source":"upload"}"""));
        _silo.On(HttpMethod.Patch, ProfilesPath + "/p-owner", ScriptedSilo.Problem(500, "internal_error"),
            ScriptedSilo.Json(HttpStatusCode.OK, """{"id":"p-owner","name":"Elena Moreau","is_primary":true,"has_pin":true,"is_child":false}"""));
        var editor = Editor(Owner with { Avatar = "preset:dicebear:identicon:cosmic-otter" });
        editor.ChoosePicture(Png, "a.png");
        editor.Pin = "2580";
        await editor.SaveAsync(Ct);

        editor.ChoosePicture(Png, "b.png");
        editor.RemoveNewPicture();
        await editor.SaveAsync(Ct);

        Assert.DoesNotContain("\"avatar\"", _silo.SentTo(HttpMethod.Patch, ProfilesPath + "/p-owner")[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LibrariesThatCouldNotBeRead_KeepSayingSo_AndCanBeReadAgain()
    {
        _silo.On(HttpMethod.Get, "api/v2/user/libraries", ScriptedSilo.NetworkError(), ScriptedSilo.Json(HttpStatusCode.OK, LibrariesJson));
        var editor = Editor();
        await editor.LoadLibrariesAsync(Ct);
        editor.Name = "Leo";
        editor.LibraryRestrictionsEnabled = true;

        Assert.Null(await editor.SaveAsync(Ct));
        Assert.True(editor.HasLibrariesLoadError);
        Assert.NotEqual("Choose at least one library.", editor.LibrariesError);
        Assert.NotEqual("", editor.LibrariesError);

        await editor.RetryLibrariesCommand.ExecuteAsync(null);

        Assert.False(editor.HasLibrariesLoadError);
        Assert.Equal(2, editor.Libraries.Count);
    }

    [Fact]
    public void AServersOwnPreset_IsDrawnFromUnderItsBasePath()
    {
        var editor = Editor(Kid with { Avatar = "preset:avatar-3" });

        Assert.Equal(new Uri("https://nas.example/silo/profile-avatars/avatar-3.svg"), editor.Preview?.Url);
        Assert.Equal("Star Bear", editor.PreviewLine);
    }

    [Fact]
    public void AStoredPreset_OpensOnItsStyle_AndMoreOptionsShowsOthers()
    {
        var editor = Editor(Kid with { Avatar = "preset:dicebear:fun-emoji:mint-panda" });
        var first = editor.Presets.Select(p => p.Id).ToList();

        editor.MorePresetsCommand.Execute(null);

        Assert.Equal(("fun-emoji", "Mint Panda"), (editor.ActiveStyle.Id, editor.SelectedPreset?.Label));
        Assert.NotEqual(first, editor.Presets.Select(p => p.Id));
        Assert.All(editor.Presets, p => Assert.StartsWith("dicebear:fun-emoji:", p.Id, StringComparison.Ordinal));
    }
}
