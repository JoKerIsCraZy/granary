using System.Net;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.Profiles;

/// <summary>The picker's Add profile, as the apps' picker: written as the household's primary profile without picking it.</summary>
public sealed class ProfilePickerAddProfileTests : IDisposable
{
    private const string ProfilesPath = "api/v2/profiles";

    private const string HouseholdJson = """
        {"items": [
          {"id": "p-owner", "name": "Elena Moreau", "is_primary": true, "has_pin": true, "is_child": false},
          {"id": "p-kid", "name": "Mila", "is_primary": false, "has_pin": false, "is_child": true}
        ], "avatar_upload_enabled": false, "max_profiles": 5}
        """;

    private readonly ScriptedSilo _silo = new();
    private readonly FakeSessionManager _session = new()
    {
        ActiveServer = ScriptedSilo.Server,
        State = SessionState.ProfileRequired,
        Account = new Account("1", "elena", "", "user", [], true, false, null),
    };
    private readonly RecordingLogger<ProfilePickerViewModel> _logger = new();
    private readonly ProfilePickerViewModel _picker;

    public ProfilePickerAddProfileTests()
    {
        _picker = new ProfilePickerViewModel(new ProfilesApi(_silo.Http), _session, new ImmediateDispatcher(), _logger);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _picker.Dispose();

    private async Task LoadAsync(string json = HouseholdJson)
    {
        _silo.On(HttpMethod.Get, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.OK, json));
        await _picker.LoadCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task AddProfile_AsksForThePrimaryProfilesPin_ThenWritesAsItWithItsProof_WithoutPickingIt()
    {
        await LoadAsync();
        _silo.On(HttpMethod.Post, ProfilesPath + "/p-owner/verify-pin", ScriptedSilo.Json(HttpStatusCode.OK, """{"valid":true,"profile_token":"pvt_owner"}"""));
        _silo.On(HttpMethod.Post, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.Created, """{"id":"p-new","name":"Leo","is_primary":false,"has_pin":false,"is_child":false}"""));

        _picker.AddProfileCommand.Execute(null);
        Assert.True(_picker.IsPinPromptOpen);
        Assert.Equal("Adding a profile needs the primary profile's PIN.", _picker.PinPromptDescription);
        await _picker.SubmitPinCommand.ExecuteAsync("2580");
        var editor = Assert.IsType<Core.ViewModels.Profiles.ProfileEditorViewModel>(_picker.Editor);
        editor.Name = "Leo";
        var saved = await editor.SaveAsync(Ct);
        await _picker.CloseEditorAsync(saved);

        var create = Assert.Single(_silo.SentTo(HttpMethod.Post, ProfilesPath));
        Assert.Equal((Core.Api.SiloAuth.Account, "p-owner", "pvt_owner"), (create.Auth, create.Lent?.ProfileId, create.Lent?.Token));
        Assert.Equal(ScriptedSilo.Base, create.Lent?.Server);
        Assert.Equal("1", create.Lent?.AccountId);
        Assert.Null(_picker.Editor);
        Assert.Empty(_session.Calls);
        Assert.Equal(2, _silo.SentTo(HttpMethod.Get, ProfilesPath).Count);
        Assert.False(_picker.IsPinPromptOpen);
    }

    [Fact]
    public async Task APrimaryProfileWithoutAPin_OpensTheEditorAtOnce()
    {
        await LoadAsync(HouseholdJson.Replace("\"is_primary\": true, \"has_pin\": true", "\"is_primary\": true, \"has_pin\": false", StringComparison.Ordinal));

        _picker.AddProfileCommand.Execute(null);

        Assert.False(_picker.IsPinPromptOpen);
        Assert.NotNull(_picker.Editor);
    }

    [Fact]
    public async Task AddProfile_IsNotOffered_ForAFullHousehold_OrOneWithoutAPrimaryProfile()
    {
        await LoadAsync(HouseholdJson.Replace("\"max_profiles\": 5", "\"max_profiles\": 2", StringComparison.Ordinal));
        Assert.False(_picker.CanAddProfile);

        await LoadAsync(HouseholdJson.Replace("\"is_primary\": true", "\"is_primary\": false", StringComparison.Ordinal));
        Assert.False(_picker.CanAddProfile);

        _picker.AddProfileCommand.Execute(null);
        Assert.Null(_picker.Editor);
    }

    [Fact]
    public async Task AWrongPrimaryPin_OpensNoEditor()
    {
        await LoadAsync();
        _silo.On(HttpMethod.Post, ProfilesPath + "/p-owner/verify-pin", ScriptedSilo.Json(HttpStatusCode.OK, """{"valid":false}"""));

        _picker.AddProfileCommand.Execute(null);
        await _picker.SubmitPinCommand.ExecuteAsync("0000");

        Assert.Null(_picker.Editor);
        Assert.True(_picker.IsPinPromptOpen);
        Assert.Empty(_session.Calls);
    }

    [Fact]
    public async Task APrimaryPinCheckThatFails_LogsNeitherTheProfileNorThePin()
    {
        await LoadAsync();
        _silo.On(HttpMethod.Post, ProfilesPath + "/p-owner/verify-pin", ScriptedSilo.Problem(503, "dependency_unavailable"));

        _picker.AddProfileCommand.Execute(null);
        await _picker.SubmitPinCommand.ExecuteAsync("2580");

        Assert.NotEmpty(_logger.Lines);
        Assert.DoesNotContain(_logger.Lines, line => line.Contains("p-owner", StringComparison.Ordinal) || line.Contains("2580", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnotherAccountSignedInBeforeThePin_OpensNoEditor()
    {
        await LoadAsync();
        _silo.On(HttpMethod.Post, ProfilesPath + "/p-owner/verify-pin", ScriptedSilo.Json(HttpStatusCode.OK, """{"valid":true,"profile_token":"pvt_owner"}"""));

        _picker.AddProfileCommand.Execute(null);
        _session.Account = new Account("2", "someone", "", "user", [], true, false, null);
        await _picker.SubmitPinCommand.ExecuteAsync("2580");

        Assert.Null(_picker.Editor);
        Assert.Equal("Couldn't open the new profile form. Try again.", _picker.Status);
    }

    [Fact]
    public async Task AnotherServer_TakesTheOpenEditorAndItsProofAway()
    {
        await LoadAsync(HouseholdJson.Replace("\"is_primary\": true, \"has_pin\": true", "\"is_primary\": true, \"has_pin\": false", StringComparison.Ordinal));
        _picker.AddProfileCommand.Execute(null);
        Assert.NotNull(_picker.Editor);

        _session.ActiveServer = ScriptedSilo.Entry("srv2", new Uri("https://other.example"));
        _session.State = SessionState.SignedOut;
        _session.RaiseChanged();

        Assert.Null(_picker.Editor);
    }

    [Fact]
    public async Task PickingAProfile_StillAsksForItsOwnPin_WithoutTheAddLine()
    {
        await LoadAsync();
        _picker.AddProfileCommand.Execute(null);
        _picker.CancelPinCommand.Execute(null);

        await _picker.SelectCommand.ExecuteAsync(_picker.Profiles[0]);

        Assert.True(_picker.IsPinPromptOpen);
        Assert.Equal("", _picker.PinPromptDescription);
    }
}
