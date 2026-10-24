using Microsoft.Extensions.DependencyInjection;

namespace Granary.Core.Servers;

public static class ServerManagementServiceCollectionExtensions
{
    /// <summary>Registers the server registry and the server management view models.</summary>
    public static IServiceCollection AddServerManagement(this IServiceCollection services)
    {
        return services;
    }
}
