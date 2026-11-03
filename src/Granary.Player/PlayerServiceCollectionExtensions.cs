using Granary.Core.Playback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Granary.Player;

public static class PlayerServiceCollectionExtensions
{
    /// <summary>Registers one libmpv engine for the app, exposed as playback engine and video output.</summary>
    public static IServiceCollection AddGranaryPlayer(this IServiceCollection services, MpvEngineOptions? options = null)
    {
        services.AddSingleton(options ?? new MpvEngineOptions());
        // Apply now reaches the engine through the handle, which knows whether the engine was made yet.
        services.AddSingleton<MpvRestartHandle>();
        services.AddSingleton<IMpvRestart>(sp => sp.GetRequiredService<MpvRestartHandle>());
        services.AddSingleton(sp =>
        {
            var engine = new MpvPlaybackEngine(sp.GetRequiredService<MpvEngineOptions>(), sp.GetRequiredService<ILogger<MpvPlaybackEngine>>(),
                sp.GetService<TimeProvider>(), sp.GetService<IMpvConfigStatus>());
            sp.GetRequiredService<MpvRestartHandle>().Attach(engine);
            return engine;
        });
        services.AddSingleton<IPlaybackEngine>(sp => sp.GetRequiredService<MpvPlaybackEngine>());
        services.AddSingleton<ICompositionVideoOutput>(sp => sp.GetRequiredService<MpvPlaybackEngine>());
        // The mpv.conf editor's check, by the engine's own rules; it does not start mpv.
        services.AddSingleton<IMpvConfigCheck, MpvConfigCheck>();
        if (OperatingSystem.IsWindows())
        {
            services.TryAddSingleton<IPlaybackDeviceProbe, MpvDeviceProbe>();
        }
        return services;
    }
}
