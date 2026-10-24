using Granary.Core;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.Servers;
using Granary.Core.ViewModels;
using Granary.Player;
using Granary.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Granary.Tests;

public sealed class CompositionRootTests
{
    [Fact]
    public void Services_ResolveAndTheEngineIsShared()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll")),
            "libmpv-2.dll not found; run scripts/fetch-native.ps1.");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IUiDispatcher, ImmediateDispatcher>();
        services.AddSingleton<INavigationService, RecordingNavigation>();
        services.AddGranaryCore();
        services.AddGranaryPlayer(new MpvEngineOptions { VideoOutput = VideoOutputKind.None });

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<IServerProbe>());
        Assert.NotNull(provider.GetRequiredService<ISecretStore>());
        Assert.NotNull(provider.GetRequiredService<AddServerViewModel>());
        Assert.NotNull(provider.GetRequiredService<ServersViewModel>());
        Assert.NotNull(provider.GetRequiredService<SignInViewModel>());
        Assert.NotNull(provider.GetRequiredService<HomeViewModel>());
        Assert.Same(provider.GetRequiredService<ShellViewModel>(), provider.GetRequiredService<ShellViewModel>());
        Assert.IsType<SessionManager>(provider.GetRequiredService<ISessionManager>());
        Assert.NotNull(provider.GetRequiredService<SiloHttp>());
        using var player = provider.GetRequiredService<PlayerViewModel>();
        Assert.Same(provider.GetRequiredService<IPlaybackEngine>(), provider.GetRequiredService<ICompositionVideoOutput>());
    }

    [Fact]
    public void SiloApiException_ExposesTheProblemWithoutItsDetail()
    {
        var problem = new ApiProblem($"{ApiProblem.TypeOrigin}{ProblemCodes.RateLimited}", "Rate limited", 429,
            "Bearer abc is not allowed", "urn:silo:request:1", null);

        var exception = new SiloApiException(429, problem, TimeSpan.FromSeconds(5));

        Assert.Equal(ProblemCodes.RateLimited, exception.Code);
        Assert.Equal(TimeSpan.FromSeconds(5), exception.RetryAfter);
        Assert.Equal("Silo API returned 429 rate_limited", exception.Message);
    }
}
