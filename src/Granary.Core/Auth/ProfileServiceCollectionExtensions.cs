using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Granary.Core.Auth;

public static class ProfileServiceCollectionExtensions
{
    /// <summary>Registers profile selection (with PIN) and password change.</summary>
    public static IServiceCollection AddProfiles(this IServiceCollection services)
    {
        services.AddTransient<ProfilePickerViewModel>();
        services.AddTransient<ChangePasswordViewModel>();
        return services;
    }
}
