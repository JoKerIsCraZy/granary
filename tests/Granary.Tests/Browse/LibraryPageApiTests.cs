using System.Net;
using Granary.Core;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels;
using Granary.Player;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using Microsoft.Extensions.DependencyInjection;

namespace Granary.Tests.Browse;

public sealed class LibraryPageApiTests
{
    private readonly ScriptedSilo _silo = new();

    private LibraryPageApi Api => new(_silo.Http);

    [Fact]
    public async Task GetCollections_ReadsGroupsUngroupedAndTheFullList()
    {
        _silo.On(HttpMethod.Get, "api/v2/library/1/collections", ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.Collections));

        var tab = await Api.GetCollectionsAsync(ScriptedSilo.Base, "1", TestContext.Current.CancellationToken);

        Assert.Equal("1", tab.LibraryId);
        var group = Assert.Single(tab.Groups);
        Assert.Equal(LibraryCollectionGroupKinds.UserCollections, group.Kind);
        Assert.Equal("p-owner", Assert.Single(group.Collections).CreatorProfileId);
        Assert.Equal(9999, tab.Ungrouped!.SortOrder);
        Assert.Equal(12, Assert.Single(tab.Ungrouped.Collections).ItemCount);
        Assert.Equal("Oscar Winners", Assert.Single(tab.Collections).Title);
        Assert.Equal(SiloAuth.Profile, Assert.Single(_silo.Sent).Auth);
    }

    [Fact]
    public async Task GetCollections_EscapesTheLibraryId()
    {
        _silo.On(HttpMethod.Get, "api/v2/library/a%2Fb/collections", ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.NoCollections));

        var tab = await Api.GetCollectionsAsync(ScriptedSilo.Base, "a/b", TestContext.Current.CancellationToken);

        Assert.Empty(tab.Groups);
        Assert.Null(tab.Ungrouped);
        Assert.Equal("/silo/api/v2/library/a%2Fb/collections", Assert.Single(_silo.Sent).Uri.AbsolutePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    public async Task GetCollections_RefusesIdsThatAreNotOne(string id) =>
        await Assert.ThrowsAsync<ArgumentException>(() => Api.GetCollectionsAsync(ScriptedSilo.Base, id, TestContext.Current.CancellationToken));

    [Fact]
    public async Task ShuffleCapability_AllowsOnlyAvailableScopes()
    {
        _silo.On(HttpMethod.Get, "api/v2/shuffles/capabilities", ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.ShuffleAvailable));

        var capability = await Api.GetShuffleCapabilityAsync(ScriptedSilo.Base, TestContext.Current.CancellationToken);

        Assert.True(capability.CanShuffle(LibraryPageApi.LibraryScope));
        Assert.False(capability.CanShuffle("playlist"));
        Assert.False(new LibraryShuffleCapability { State = "available", Allowed = false, ScopeKinds = ["library"] }.CanShuffle("library"));
        Assert.False(new LibraryShuffleCapability { State = "disabled", Allowed = true, ScopeKinds = ["library"] }.CanShuffle("library"));
    }

    [Fact]
    public async Task StartLibraryShuffle_PostsTheLibraryScope()
    {
        _silo.On(HttpMethod.Post, "api/v2/shuffles", ScriptedSilo.Json(HttpStatusCode.Created, LibraryPayloads.Shuffle));

        var shuffle = await Api.StartLibraryShuffleAsync(ScriptedSilo.Base, "1", TestContext.Current.CancellationToken);

        Assert.Equal("17", shuffle.Id);
        Assert.Equal("movie:alien-1979", shuffle.Current!.ContentId);
        Assert.Equal("movie", shuffle.Current.Type);
        Assert.Equal("""{"scope":{"kind":"library","id":"1"}}""", Assert.Single(_silo.Sent).Body);
    }

    [Fact]
    public async Task StartLibraryShuffle_NothingPlayable_IsAConflict()
    {
        _silo.On(HttpMethod.Post, "api/v2/shuffles", ScriptedSilo.Problem(409, ProblemCodes.Conflict));

        var error = await Assert.ThrowsAsync<SiloApiException>(() => Api.StartLibraryShuffleAsync(ScriptedSilo.Base, "1", TestContext.Current.CancellationToken));

        Assert.Equal(409, error.Status);
    }

    [Fact]
    public void AddGranaryCore_RegistersTheLibraryPageAsTransientAndResolvable()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IUiDispatcher, ImmediateDispatcher>();
        services.AddSingleton<INavigationService, RecordingNavigation>();
        services.AddSingleton<IConfirmation, FakeConfirmation>();
        services.AddGranaryCore();
        services.AddGranaryPlayer(new MpvEngineOptions { VideoOutput = VideoOutputKind.None });

        // Validation builds every constructor call without creating anything.
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.Equal(ServiceLifetime.Transient, Assert.Single(services, d => d.ServiceType == typeof(LibraryViewModel)).Lifetime);
        Assert.Equal(ServiceLifetime.Transient, Assert.Single(services, d => d.ServiceType == typeof(LibraryPageApi)).Lifetime);
    }
}
