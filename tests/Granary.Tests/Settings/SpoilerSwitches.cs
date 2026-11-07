using Granary.Core.Api.Settings;
using Granary.Core.Settings;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Settings;

/// <summary>The profile's spoiler switches, read from a server of their own and ready to change, for the places that apply them.</summary>
internal static class SpoilerSwitches
{
    public static async Task<EpisodeSpoilerSettings> StartedAsync(bool images = true, bool overviews = true)
    {
        var harness = new LibraryHarness();
        harness.Answer("api/v2/settings/values/effective", EpisodeSpoilerSettingsTests.Answer(images, overviews));
        harness.Silo.On(HttpMethod.Put, "api/v2/settings/values/" + EpisodeSpoilerSettings.ImagesKey, ScriptedSilo.NoContent());
        harness.Silo.On(HttpMethod.Put, "api/v2/settings/values/" + EpisodeSpoilerSettings.OverviewsKey, ScriptedSilo.NoContent());
        var settings = new EpisodeSpoilerSettings(harness.Session, new SettingsApi(harness.Silo.Http), new ImmediateDispatcher(),
            harness.Logs.For<EpisodeSpoilerSettings>());
        await settings.StartAsync();
        return settings;
    }
}
