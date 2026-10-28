using System.Net;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Settings;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Catalog;

/// <summary>The libraries a profile browses, as the web's useUserLibraries: its hidden ones left out, its order applied.</summary>
public sealed class VisibleLibrariesTests
{
    private const string LibrariesPath = "api/v2/user/libraries";
    private const string SettingsPath = "api/v2/settings/values/effective";

    private const string Libraries = """
        { "items": [
            { "id": "3", "name": "Anime", "type": "series", "sort_order": 3 },
            { "id": "1", "name": "Filme", "type": "movies", "sort_order": 1 },
            { "id": "2", "name": "Serien", "type": "series", "sort_order": 2 }
        ] }
        """;

    private readonly ScriptedSilo _silo = new();
    private readonly LogSink _logs = new();

    public VisibleLibrariesTests() => _silo.On(HttpMethod.Get, LibrariesPath, ScriptedSilo.Json(HttpStatusCode.OK, Libraries));

    private VisibleLibraries Create() => new(new CatalogApi(_silo.Http), new SettingsApi(_silo.Http), _logs.For<VisibleLibraries>());

    private void Settings(string hidden, string order) => _silo.On(HttpMethod.Get, SettingsPath, ScriptedSilo.Json(HttpStatusCode.OK, $$"""
        { "items": [
            { "key": "ui.disabled_library_ids", "value": {{hidden}}, "source": "profile" },
            { "key": "ui.library_order", "value": {{order}}, "source": "default" }
        ], "revision": 4 }
        """));

    private static Library Library(string id) => new(id, "L" + id, "movies", int.Parse(id, System.Globalization.CultureInfo.InvariantCulture), null);

    [Fact]
    public async Task TheHiddenLibraries_AreLeftOut_InTheServersOrder()
    {
        Settings("[2]", "null");

        var libraries = await Create().ListAsync(ScriptedSilo.Base, TestContext.Current.CancellationToken);

        Assert.Equal(["Filme", "Anime"], libraries.Items.Select(l => l.Name));
        Assert.True(libraries.PreferencesRead);
        var read = Assert.Single(_silo.SentTo(HttpMethod.Get, SettingsPath));
        Assert.Equal("?keys=ui.disabled_library_ids&keys=ui.library_order", read.Uri.Query);
    }

    [Fact]
    public async Task TheProfilesOrder_ComesFirst_TheRestAfterInTheServersOrder()
    {
        Settings("null", "[3, 2]");

        var libraries = await Create().ListAsync(ScriptedSilo.Base, TestContext.Current.CancellationToken);

        Assert.Equal(["Anime", "Serien", "Filme"], libraries.Items.Select(l => l.Name));
    }

    [Fact]
    public async Task PreferencesThatCannotBeRead_HideNothing()
    {
        _silo.On(HttpMethod.Get, SettingsPath, ScriptedSilo.Problem(422, "validation_failed"));

        var libraries = await Create().ListAsync(ScriptedSilo.Base, TestContext.Current.CancellationToken);

        Assert.Equal(["Filme", "Serien", "Anime"], libraries.Items.Select(l => l.Name));
        Assert.False(libraries.PreferencesRead);
    }

    [Fact]
    public async Task ALibraryListThatFails_Fails()
    {
        Settings("[2]", "null");
        _silo.On(HttpMethod.Get, LibrariesPath, ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable));

        await Assert.ThrowsAsync<SiloApiException>(() => Create().ListAsync(ScriptedSilo.Base, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("[3, 1]", "3,1")]
    [InlineData("\"[2,2,7]\"", "2,7")]
    [InlineData("[0, -4, 1.5, \"5\", 6, null]", "6")]
    [InlineData("null", "")]
    [InlineData("\"\"", "")]
    [InlineData("\"not json\"", "")]
    [InlineData("{ \"ids\": [1] }", "")]
    public void Ids_AreReadAsTheWebReadsThem(string json, string expected)
    {
        var ids = VisibleLibraries.ParseIds(JsonDocument.Parse(json).RootElement);

        Assert.Equal(expected, string.Join(',', ids));
    }

    [Fact]
    public void AnOrderNamingALibraryNoLongerThere_KeepsTheOthers()
    {
        var arranged = VisibleLibraries.Arrange([Library("1"), Library("2"), Library("3")], hidden: ["1"], order: ["9", "3"]);

        Assert.Equal(["3", "2"], arranged.Select(l => l.Id));
    }
}
