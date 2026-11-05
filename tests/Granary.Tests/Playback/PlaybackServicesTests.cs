using Granary.Core;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Granary.Tests.Playback;

public sealed class PlaybackServicesTests
{
    [Theory]
    [InlineData(typeof(WatchApi))]
    [InlineData(typeof(PlaybackApi))]
    [InlineData(typeof(TrackPreferencesApi))]
    public void AddGranaryCore_RegistersTheApiAsTransient(Type api)
    {
        var services = new ServiceCollection().AddGranaryCore();

        var descriptor = Assert.Single(services, d => d.ServiceType == api);
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
    }

    [Theory]
    [InlineData(5, 4, false)]
    [InlineData(5, 5, true)]
    [InlineData(0, 50, false)]
    public void TheRegisteredStreak_FollowsThisPcsStillWatchingChoice(int threshold, int automatic, bool gated)
    {
        var services = new ServiceCollection().AddGranaryCore();
        services.AddSingleton<IPlaybackDevicePreferences>(new InMemoryPlaybackDevicePreferences { PassoutThreshold = threshold });
        using var provider = services.BuildServiceProvider();
        var streak = provider.GetRequiredService<AutoPlayStreak>();

        for (int episode = 0; episode < automatic; episode++)
        {
            streak.RecordAutomatic();
        }

        Assert.Equal(gated, streak.IsGated);
    }
}
