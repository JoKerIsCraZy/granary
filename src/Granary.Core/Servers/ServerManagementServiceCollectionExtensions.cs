using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Servers;

public static class ServerManagementServiceCollectionExtensions
{
    /// <summary>Registers the server registry and the server management view models.</summary>
    public static IServiceCollection AddServerManagement(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IServerRegistry>(sp => new JsonServerRegistry(
            JsonServerRegistry.DefaultPath,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<JsonServerRegistry>>()));
        services.AddTransient<ServersViewModel>();
        return services;
    }
}
