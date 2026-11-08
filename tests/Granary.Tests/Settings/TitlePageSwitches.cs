using Granary.Core.Settings;
using Granary.Tests.Browse;
using Granary.Tests.Profiles;

namespace Granary.Tests.Settings;

/// <summary>The profile's Title Pages settings, read from a server of their own and ready to change, for the pages that apply them.</summary>
internal static class TitlePageSwitches
{
    public static async Task<TitlePageSettings> StartedAsync(string answer)
    {
        var settings = Unread(answer);
        await settings.StartAsync();
        return settings;
    }

    /// <summary>The settings before their first read (<see cref="TitlePageState.Loading"/>); <c>StartAsync</c> reads them.</summary>
    public static TitlePageSettings Unread(string answer)
    {
        var harness = new LibraryHarness();
        harness.Answer(TitlePageSettingsTests.EffectivePath, answer);
        foreach (string key in new[] { TitlePageSettings.AdvisoryAgeKey, TitlePageSettings.TitleArtKey })
        {
            harness.Silo.On(HttpMethod.Put, "api/v2/settings/values/" + key, ScriptedSilo.NoContent());
            harness.Silo.On(HttpMethod.Delete, "api/v2/settings/values/" + key, ScriptedSilo.NoContent());
        }
        return TitlePageSettingsTests.Create(harness);
    }
}
