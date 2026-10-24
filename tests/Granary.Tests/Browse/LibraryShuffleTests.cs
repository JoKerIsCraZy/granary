using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.ViewModels;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

public sealed class LibraryShuffleTests
{
    private const string CapabilityPath = "api/v2/shuffles/capabilities";
    private const string ShufflesPath = "api/v2/shuffles";

    private readonly LibraryHarness _harness = new();

    [Fact]
    public async Task Shuffle_OpensTheFirstRandomPick()
    {
        _harness.Silo.On(HttpMethod.Post, ShufflesPath, ScriptedSilo.Json(HttpStatusCode.Created, LibraryPayloads.Shuffle));
        var library = _harness.Create();
        await library.ActivateAsync("1");
        Assert.True(library.CanShuffle);

        await library.ShuffleCommand.ExecuteAsync(null);

        var sent = Assert.Single(_harness.Silo.SentTo(HttpMethod.Post, ShufflesPath));
        Assert.Equal("""{"scope":{"kind":"library","id":"1"}}""", sent.Body);
        Assert.Equal(SiloAuth.Profile, sent.Auth);
        Assert.Equal((AppRoute.Item, (object?)"movie:alien-1979"), _harness.Navigation.Navigations[^1]);
        Assert.False(library.HasShuffleMessage);
    }

    [Fact]
    public async Task ServerThatCannotShuffle_HidesShuffle()
    {
        _harness.Answer(CapabilityPath, LibraryPayloads.ShuffleNotConfigured);
        var library = _harness.Create();

        await library.ActivateAsync("1");

        Assert.False(library.CanShuffle);
    }

    [Fact]
    public async Task AudiobookLibrary_HasNoShuffle()
    {
        _harness.Answer("api/v2/library/3/sections", LibraryPayloads.Sections);
        _harness.Answer("api/v2/library/3/sections/recently_added/items", LibraryPayloads.RecentlyAddedItems);
        var library = _harness.Create();

        await library.ActivateAsync("3");

        Assert.Equal("LIBRARY / HÖRBÜCHER", library.Breadcrumb);
        Assert.False(library.CanShuffle);
        Assert.Equal(0, _harness.Count(CapabilityPath));
    }

    [Fact]
    public async Task Capability_IsAskedOncePerServerAndProfile()
    {
        _harness.Answer("api/v2/library/2/sections", LibraryPayloads.Sections);
        _harness.Answer("api/v2/library/2/sections/recently_added/items", LibraryPayloads.RecentlyAddedItems);
        var library = _harness.Create();

        await library.ActivateAsync("1");
        await library.ActivateAsync("2");

        Assert.True(library.CanShuffle);
        Assert.Equal(1, _harness.Count(CapabilityPath));
    }

    [Fact]
    public async Task FailedCapability_HidesShuffleAndAsksAgainNextTime()
    {
        _harness.Answer(CapabilityPath, ScriptedSilo.NetworkError(), ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.ShuffleAvailable));
        _harness.Answer("api/v2/library/2/sections", LibraryPayloads.Sections);
        _harness.Answer("api/v2/library/2/sections/recently_added/items", LibraryPayloads.RecentlyAddedItems);
        var library = _harness.Create();

        await library.ActivateAsync("1");
        Assert.False(library.CanShuffle);
        await library.ActivateAsync("2");

        Assert.True(library.CanShuffle);
        Assert.Equal(LibraryLoadState.Ready, library.State);
    }

    [Fact]
    public async Task NothingPlayable_SaysSoAndStays()
    {
        _harness.Silo.On(HttpMethod.Post, ShufflesPath, ScriptedSilo.Problem(409, ProblemCodes.Conflict));
        var library = _harness.Create();
        await library.ActivateAsync("1");
        int navigations = _harness.Navigation.Navigations.Count;

        await library.ShuffleCommand.ExecuteAsync(null);

        Assert.Equal(LibraryViewModel.ShuffleNothingPlayable, library.ShuffleMessage);
        Assert.True(library.HasShuffleMessage);
        Assert.Equal(navigations, _harness.Navigation.Navigations.Count);

        library.HasShuffleMessage = false;

        Assert.Equal("", library.ShuffleMessage);
    }

    [Fact]
    public async Task FailedShuffle_IsWordedWithoutTheExceptionText()
    {
        _harness.Silo.On(HttpMethod.Post, ShufflesPath, ScriptedSilo.NetworkError());
        var library = _harness.Create();
        await library.ActivateAsync("1");

        await library.ShuffleCommand.ExecuteAsync(null);

        Assert.Equal(LibraryViewModel.ShuffleFailed, library.ShuffleMessage);
    }

    [Fact]
    public async Task ShuffleAnsweredAfterLeaving_DoesNotNavigate()
    {
        var release = new TaskCompletionSource();
        _harness.Silo.On(HttpMethod.Post, ShufflesPath, async () =>
        {
            await release.Task;
            return await ScriptedSilo.Json(HttpStatusCode.Created, LibraryPayloads.Shuffle)();
        });
        var library = _harness.Create();
        await library.ActivateAsync("1");
        int navigations = _harness.Navigation.Navigations.Count;

        var shuffle = library.ShuffleCommand.ExecuteAsync(null);
        library.Deactivate();
        release.SetResult();
        await shuffle;

        Assert.Equal(navigations, _harness.Navigation.Navigations.Count);
    }
}
