using Granary.Core;
using Granary.Core.Api.Playback;
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
}
