using Granary.Core.Playback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Granary.Player;

public static class PlayerServiceCollectionExtensions
{
    /// <summary>Registers one libmpv engine for the app, exposed as playback engine and video output.</summary>
    public static IServiceCollection AddGranaryPlayer(this IServiceCollection services, MpvEngineOptions? options = null)
    {
        services.AddSingleton(options ?? new MpvEngineOptions());
        services.AddSingleton<MpvPlaybackEngine>();
        services.AddSingleton<IPlaybackEngine>(sp => sp.GetRequiredService<MpvPlaybackEngine>());
        services.AddSingleton<ICompositionVideoOutput>(sp => sp.GetRequiredService<MpvPlaybackEngine>());
        if (OperatingSystem.IsWindows())
        {
            services.TryAddSingleton<IPlaybackDeviceProbe, MpvDeviceProbe>();
        }
        return services;
    }
}
