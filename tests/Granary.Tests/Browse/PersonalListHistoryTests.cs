using System.Net;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>History's Select and Remove, as the web's catalog page offers them there.</summary>
public sealed class PersonalListHistoryTests
{
    private const string RemovePath = "api/v2/history/remove";

    private readonly LibraryHarness _harness = new();
    private readonly ViewerStateChanges _changes = new(new PlaybackStops());

    public PersonalListHistoryTests()
    {
        _harness.Answer(WatchlistTitlesViewModelTests.StatusPath, """{ "requests_enabled": false }""");
        _harness.Silo.On(HttpMethod.Post, RemovePath, ScriptedSilo.NoContent());
    }

    private async Task<PersonalListViewModel> HistoryAsync()
    {
        var list = WatchlistTitlesViewModelTests.CreateList(_harness, _changes);
        await list.ActivateAsync("history");
        return list;
    }

    private MediaCard Card(PersonalListViewModel list, string title) => list.Grid.Cards.Single(c => c.Title == title);

    private JsonElement RemoveBody(int index = 0) => JsonDocument.Parse(_harness.Silo.SentTo(HttpMethod.Post, RemovePath)[index].Body!).RootElement;

    [Fact]
    public async Task History_OffersSelect_TheOtherListsDoNot()
    {
        var list = await HistoryAsync();
        Assert.True(list.IsHistory);

        await list.ActivateAsync("favorites");

        Assert.False(list.IsHistory);
    }

    [Fact]
    public async Task Selecting_PicksAndDropsTitles()
    {
        var list = await HistoryAsync();

        list.StartSelectingCommand.Execute(null);
        list.Selection.Toggle(Card(list, "Alien"));
        list.Selection.Toggle(Card(list, "Heat"));
        list.Selection.Toggle(Card(list, "Heat"));

        Assert.True(list.Selection.IsActive);
        Assert.Equal("1 selected", list.Selection.CountText);
        Assert.True(list.Selection.Contains(Card(list, "Alien").Id));
        Assert.Equal("Remove watch data?", list.RemovalTitle);
        Assert.Equal("This clears the item's watch history, watched status, and resume progress for this profile.", list.RemovalDescription);
    }

    [Fact]
    public async Task SelectLoaded_ClearAndDone()
    {
        var list = await HistoryAsync();
        list.StartSelectingCommand.Execute(null);

        list.SelectLoadedCommand.Execute(null);
        Assert.Equal(2, list.Selection.Count);
        Assert.Equal("Remove selected watch data?", list.RemovalTitle);
        Assert.Equal("2 selected items will have their watch history, watched status, and resume progress cleared for this profile.", list.RemovalDescription);

        list.ClearSelectionCommand.Execute(null);
        Assert.False(list.Selection.HasPicks);
        Assert.True(list.Selection.IsActive);

        list.SelectLoadedCommand.Execute(null);
        list.StopSelectingCommand.Execute(null);
        Assert.False(list.Selection.IsActive);
        Assert.Equal(0, list.Selection.Count);
    }

    [Fact]
    public void ASeriesOrSeason_GoesAsTheWholeShow()
    {
        Assert.Equal(new HistoryRemovalTarget("series:dark", "show"), HistoryRemovalTarget.For("series:dark", "series"));
        Assert.Equal(new HistoryRemovalTarget("season:dark-1", "show"), HistoryRemovalTarget.For("season:dark-1", "season"));
        Assert.Equal(new HistoryRemovalTarget("episode:dark-s01e01", "item"), HistoryRemovalTarget.For("episode:dark-s01e01", "episode"));
        Assert.Equal(new HistoryRemovalTarget("movie:heat", "item"), HistoryRemovalTarget.For("movie:heat", "movie"));
    }

    [Fact]
    public async Task Remove_ClearsThePicks_TellsTheApp_AndLoadsAgain()
    {
        var list = await HistoryAsync();
        long version = _changes.Version;
        int loads = _harness.Count(LibraryHarness.CatalogPath);
        list.StartSelectingCommand.Execute(null);
        list.SelectLoadedCommand.Execute(null);

        await list.RemoveSelectedCommand.ExecuteAsync(null);

        var targets = RemoveBody().GetProperty("targets").EnumerateArray().ToList();
        Assert.Equal(["movie:alien-1979", Card(list, "Heat").Id], targets.Select(t => t.GetProperty("content_id").GetString()));
        Assert.All(targets, t => Assert.Equal("item", t.GetProperty("scope").GetString()));
        Assert.Equal("Removed watch data for 2 items", list.RemovalNotice);
        Assert.False(list.RemovalFailed);
        Assert.False(list.Selection.IsActive);
        Assert.Equal(version + 1, _changes.Version);
        Assert.Equal(loads + 1, _harness.Count(LibraryHarness.CatalogPath));
    }

    [Fact]
    public async Task RemoveOne_SaysSo()
    {
        var list = await HistoryAsync();
        list.StartSelectingCommand.Execute(null);
        list.Selection.Toggle(Card(list, "Alien"));

        await list.RemoveSelectedCommand.ExecuteAsync(null);

        Assert.Equal("Removed watch data", list.RemovalNotice);
    }

    [Fact]
    public async Task FailedRemove_KeepsThePicks_AndSaysWhy()
    {
        _harness.Silo.On(HttpMethod.Post, RemovePath, ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable));
        var list = await HistoryAsync();
        list.StartSelectingCommand.Execute(null);
        list.Selection.Toggle(Card(list, "Alien"));

        await list.RemoveSelectedCommand.ExecuteAsync(null);

        Assert.True(list.RemovalFailed);
        Assert.StartsWith("Failed to remove history.", list.RemovalNotice, StringComparison.Ordinal);
        Assert.True(list.Selection.IsActive);
        Assert.Equal(1, list.Selection.Count);
        Assert.False(list.IsRemoving);
    }

    [Fact]
    public async Task NothingPicked_SendsNothing()
    {
        var list = await HistoryAsync();
        list.StartSelectingCommand.Execute(null);

        await list.RemoveSelectedCommand.ExecuteAsync(null);

        Assert.Empty(_harness.Silo.SentTo(HttpMethod.Post, RemovePath));
    }

    [Fact]
    public async Task Placeholders_AreNotPicked_AndPicksNeedTheSelecting()
    {
        var list = await HistoryAsync();

        list.Selection.Toggle(Card(list, "Alien"));
        Assert.Equal(0, list.Selection.Count);

        list.StartSelectingCommand.Execute(null);
        list.Selection.Toggle(MediaCard.Placeholder);
        Assert.Equal(0, list.Selection.Count);
    }

    [Fact]
    public async Task AnotherList_LeavesTheSelectingBehind()
    {
        var list = await HistoryAsync();
        list.StartSelectingCommand.Execute(null);
        list.SelectLoadedCommand.Execute(null);

        await list.ActivateAsync("watchlist");

        Assert.False(list.Selection.IsActive);
        Assert.Equal(0, list.Selection.Count);
    }
}
