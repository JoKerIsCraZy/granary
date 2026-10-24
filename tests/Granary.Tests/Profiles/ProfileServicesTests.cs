using Granary.Core.Abstractions;
using Granary.Core.Api.Accounts;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Granary.Tests.Profiles;

public sealed class ProfileServicesTests
{
    [Fact]
    public void AddProfiles_RegistersBothViewModelsAsTransient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IUiDispatcher, ImmediateDispatcher>();
        services.AddSingleton<ISessionManager, FakeSessionManager>();
        services.AddSingleton(new ScriptedSilo().Http);
        services.AddTransient<ProfilesApi>();
        services.AddTransient<AccountApi>();

        services.AddProfiles();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var picker = provider.GetRequiredService<ProfilePickerViewModel>();
        using var another = provider.GetRequiredService<ProfilePickerViewModel>();
        Assert.NotSame(picker, another);
        Assert.NotSame(provider.GetRequiredService<ChangePasswordViewModel>(), provider.GetRequiredService<ChangePasswordViewModel>());
    }
}
