using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.Profiles;
using Granary.Core.ViewModels.Collections;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>A profile's own smart collection: its rules, their live preview, and saving them as the web does.</summary>
public sealed class SmartCollectionEditorTests
{
    private const string CollectionPath = "api/v2/collections/s1";
    private const string PreviewPath = "api/v2/collections/preview";

    /// <summary>Comedies (with a member this app does not know) from the 90s, a rule it can't show, and the server's no-limit stand-in.</summary>
    private const string Saved = """
        { "id": "s1", "name": "90s Comedies", "description": "", "collection_type": "smart", "creator_profile_id": "7",
          "is_shared": false, "include_in_server_collections": false, "item_count": 40,
          "query_definition": { "library_ids": [1], "media_scope": "movie", "match": "all", "groups": [ { "match": "all", "rules": [
              { "field": "genre", "op": "is", "value": "Comedy", "note": "kept" },
              { "field": "year", "op": "between", "value": [1990, 1999] },
              { "field": "mystery", "op": "is", "value": "x" } ] } ],
            "sort": { "field": "title", "order": "asc" }, "limit": 10000000 },
          "sort_config": { "order": "desc", "field": "year", "x": 1 } }
        """;

    private const string Preview = """
        { "items": [ { "content_id": "movie:a", "title": "Airplane!", "type": "movie", "poster_url": "/api/v2/artwork/a.webp" } ], "total": 40 }
        """;

    private readonly LibraryHarness _harness = new();

    public SmartCollectionEditorTests()
    {
        _harness.Silo.On(HttpMethod.Get, CollectionPath, Versioned(Saved, "\"rev-1\""));
        _harness.Silo.On(HttpMethod.Post, PreviewPath, ScriptedSilo.Json(HttpStatusCode.OK, Preview));
        _harness.Answer("api/v2/collections/capabilities", """{ "item_reorder": true }""");
        _harness.Answer("api/v2/catalog/search/capabilities", """{ "state": "available", "allowed": true, "extended_query_rules": true }""");
        _harness.Answer("api/v2/catalog/filters", LibraryPayloads.Filters);
        _harness.Answer("api/v2/profiles", """{ "items": [ { "id": "7", "name": "Elena", "is_primary": true } ], "avatar_upload_enabled": false }""");
    }

    private static Func<Task<HttpResponseMessage>> Versioned(string json, string etag) => () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return Task.FromResult(response);
    };

    private CollectionEditorViewModel Create() => new(_harness.Session, new CollectionsApi(_harness.Silo.Http), new CatalogApi(_harness.Silo.Http),
        new ImageParameterCache(new ImagesApi(_harness.Silo.Http), _harness.Logs.For<ImageParameterCache>()), new ProfilesApi(_harness.Silo.Http),
        _harness.Libraries(), _harness.Navigation, _harness.Clock, _harness.Logs.For<CollectionEditorViewModel>());

    private async Task<CollectionEditorViewModel> EditAsync()
    {
        var editor = Create();
        await editor.ActivateAsync("s1");
        await editor.Previewing;
        return editor;
    }

    private JsonElement Sent(HttpMethod method, string path) => JsonDocument.Parse(_harness.Silo.SentTo(method, path)[^1].Body!).RootElement;

    private static SmartRuleViewModel Rule(CollectionEditorViewModel editor, int index) => editor.Rules.Groups[0].Rules[index];

    [Fact]
    public async Task ASmartCollection_ShowsItsRules_AndWhatTheyMatch()
    {
        var editor = await EditAsync();

        Assert.True(editor.IsSmart);
        Assert.Equal("Smart", editor.KindLabel);
        Assert.Equal("movie", editor.Rules.Scope.Value);
        Assert.Equal("Filme", editor.Rules.LibrariesText);
        Assert.Equal("from the", editor.Rules.FromText);
        Assert.Equal("library", editor.Rules.LibraryWord);
        Assert.Equal("Comedy", Rule(editor, 0).Text);
        Assert.True(Rule(editor, 0).ShowsSuggest);
        Assert.Equal(("1990", "1999"), (Rule(editor, 1).From, Rule(editor, 1).To));
        Assert.True(Rule(editor, 2).IsLocked);
        Assert.Equal("mystery is \"x\"", Rule(editor, 2).LockedText);
        Assert.Equal(("title", "A–Z"), (editor.Rules.SortField.Value, editor.Rules.Direction.Label));
        Assert.Equal("", editor.Rules.LimitText);
        Assert.Equal("A saved default sort, Year, newest first, wins over this Order.", editor.Rules.StoredSortLine);
        Assert.Equal("40 titles · Updates itself as titles are added", editor.MetaLine);
        Assert.False(editor.ShowsSaveBar);
        Assert.Equal("40 titles match", editor.PreviewCount);
        Assert.Equal("showing 1", editor.PreviewShowing);
        Assert.Equal("Airplane!", Assert.Single(editor.PreviewTiles).Title);
    }

    [Fact]
    public async Task AChangedRule_IsSaved_WithWhatTheAppDoesNotKnowKept()
    {
        var editor = await EditAsync();
        _harness.Silo.On(HttpMethod.Patch, CollectionPath, Versioned(Saved, "\"rev-2\""));

        Rule(editor, 0).Text = "Drama";

        Assert.Equal(["Rules"], editor.ChangedFields);
        Assert.Equal("Rules not saved", editor.SaveBarMessage);
        await editor.SaveCommand.ExecuteAsync(null);

        var body = Sent(HttpMethod.Patch, CollectionPath);
        var query = body.GetProperty("query_definition");
        Assert.Equal("""{"field":"genre","op":"is","value":"Drama","note":"kept"}""", query.GetProperty("groups")[0].GetProperty("rules")[0].GetRawText());
        Assert.Equal("""{"field":"mystery","op":"is","value":"x"}""", query.GetProperty("groups")[0].GetProperty("rules")[2].GetRawText());
        Assert.Equal("[1990,1999]", query.GetProperty("groups")[0].GetProperty("rules")[1].GetProperty("value").GetRawText());
        Assert.Equal("[1]", query.GetProperty("library_ids").GetRawText());
        Assert.False(query.TryGetProperty("limit", out _));
        Assert.Equal("""{"field":"year","order":"desc","x":1}""", body.GetProperty("sort_config").GetRawText());
        Assert.False(body.TryGetProperty("display_query_definition", out _));
    }

    [Fact]
    public async Task AnotherOrder_ClearsTheSavedDefaultSort()
    {
        var editor = await EditAsync();
        _harness.Silo.On(HttpMethod.Patch, CollectionPath, Versioned(Saved, "\"rev-2\""));

        editor.Rules.SortField = editor.Rules.Sorts.First(s => s.Value == "year");

        Assert.Equal("Newest first", editor.Rules.Direction.Label);
        Assert.False(editor.Rules.HasStoredSort);
        Assert.Equal("Rules and order not saved", editor.SaveBarMessage);
        await editor.SaveCommand.ExecuteAsync(null);
        var body = Sent(HttpMethod.Patch, CollectionPath);
        Assert.Equal("""{"x":1}""", body.GetProperty("sort_config").GetRawText());
        Assert.Equal("""{"field":"year","order":"desc"}""", body.GetProperty("query_definition").GetProperty("sort").GetRawText());
    }

    [Fact]
    public async Task ANewSmartCollection_IsMadeWithItsRules()
    {
        _harness.Silo.On(HttpMethod.Post, "api/v2/collections", ScriptedSilo.Json(HttpStatusCode.Created, Saved));
        var editor = Create();
        await editor.ActivateAsync(null, PersonalCollectionTypes.Smart);
        await editor.Previewing;

        Assert.True(editor.IsSmart);
        Assert.True(editor.Rules.HasNoRules);
        Assert.Equal("Updates itself as titles are added", editor.MetaLine);
        Assert.Equal("Name it, then create it.", editor.SaveBarHint);
        editor.Name = "Comedies";
        Assert.Equal("40 titles match now. New ones join on their own.", editor.SaveBarHint);

        editor.Rules.AddRuleCommand.Execute(null);
        Rule(editor, 0).Text = "Comedy";
        await editor.SaveCommand.ExecuteAsync(null);

        var body = Sent(HttpMethod.Post, "api/v2/collections");
        Assert.Equal("smart", body.GetProperty("collection_type").GetString());
        Assert.Equal("""{"library_ids":[],"match":"all","groups":[{"match":"all","rules":[{"field":"genre","op":"is","value":"Comedy"}]}],"sort":{"field":"added_at","order":"desc"}}""",
            body.GetProperty("query_definition").GetRawText());
        Assert.Equal("{}", body.GetProperty("sort_config").GetRawText());
        Assert.Equal("s1", editor.CollectionId);
    }

    [Fact]
    public async Task ThePreview_FollowsTheRules_AfterAPause()
    {
        var editor = await EditAsync();
        int Previews() => _harness.Silo.SentTo(HttpMethod.Post, PreviewPath).Count;
        int before = Previews();

        Rule(editor, 0).Text = "Drama";
        _harness.Clock.Advance(CollectionEditorViewModel.PreviewDelay - TimeSpan.FromMilliseconds(1));
        Assert.Equal(before, Previews());
        Assert.True(editor.IsPreviewRefreshing);
        _harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await editor.Previewing;

        Assert.Equal(before + 1, Previews());
        Assert.Contains("Drama", Sent(HttpMethod.Post, PreviewPath).GetProperty("query_definition").GetRawText(), StringComparison.Ordinal);
        Assert.Equal(24, Sent(HttpMethod.Post, PreviewPath).GetProperty("limit").GetInt32());
        Assert.False(editor.IsPreviewRefreshing);
    }

    [Fact]
    public async Task APreviewThatFails_SaysSavingStillWorks()
    {
        _harness.Silo.On(HttpMethod.Post, PreviewPath, ScriptedSilo.Problem(503, "dependency_unavailable"));

        var editor = await EditAsync();

        Assert.True(editor.HasPreviewFailed);
        Assert.True(editor.SaveCommand.CanExecute(null) || !editor.ShowsSaveBar);
    }

    [Fact]
    public async Task ARulesField_ConditionAndValue_FollowEachOther()
    {
        var editor = await EditAsync();
        var rule = Rule(editor, 0);

        rule.Field = rule.Fields.First(f => f.Value == "year");
        Assert.Equal(("is", "0", true), (rule.Operator!.Value, rule.Text, rule.ShowsText));
        rule.Operator = rule.Operators.First(o => o.Value == "between");
        Assert.True(rule.ShowsRange);
        rule.From = "1980";
        rule.To = "x";
        Assert.Equal("[1980,\"x\"]", rule.ToRule().Value.GetRawText());

        rule.Field = rule.Fields.First(f => f.Value == "added_at");
        Assert.True(rule.ShowsInLast);
        rule.InLastCount = "30";
        Assert.Equal("\"30d\"", rule.ToRule().Value.GetRawText());

        rule.Field = rule.Fields.First(f => f.Value == "watched");
        Assert.True(rule.ShowsChoice);
        rule.Choice = rule.Options[0];
        Assert.Equal("true", rule.ToRule().Value.GetRawText());
        Assert.Equal("note", Assert.Single(rule.ToRule().Extra!).Key);
    }

    [Fact]
    public async Task AnotherGroup_JoinsWithOr()
    {
        var editor = await EditAsync();

        editor.Rules.AddGroupCommand.Execute(null);

        Assert.Equal(2, editor.Rules.Groups.Count);
        Assert.Equal("any", editor.Rules.ToQuery().Match);
        Assert.Equal("Add an “or” group", editor.Rules.AddGroupText);
        Assert.True(editor.Rules.Groups[1].IsLater);
        editor.Rules.Groups[1].Join = SmartRulesViewModel.Joins[0];
        Assert.Equal("all", editor.Rules.ToQuery().Match);
        editor.Rules.RemoveGroupCommand.Execute(editor.Rules.Groups[1]);
        Assert.Single(editor.Rules.Groups);
    }

    [Fact]
    public async Task AFacetRule_SuggestsTheValuesTitlesHave()
    {
        var editor = await EditAsync();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var rule = Rule(editor, 0);

        rule.Text = "dr";
        await editor.Rules.SuggestAsync(rule, TestContext.Current.CancellationToken);

        Assert.Equal(["Drama"], rule.Suggestions);
    }

    /// <summary>Values the controls would rewrite: text numbers, 1.50, a list of genres, an option not offered, "12h".</summary>
    private const string Awkward = """
        { "id": "s1", "name": "Odd rules", "description": "", "collection_type": "smart", "creator_profile_id": "7", "is_shared": false,
          "item_count": 3,
          "query_definition": { "library_ids": [], "match": "all", "groups": [ { "match": "all", "rules": [
              { "field": "year", "op": "gte", "value": "1990" },
              { "field": "rating_imdb", "op": "gte", "value": 1.50 },
              { "field": "genre", "op": "is", "value": ["Comedy", "Drama"] },
              { "field": "type", "op": "is", "value": "anime" },
              { "field": "added_at", "op": "in_last", "value": "12h" } ] } ],
            "sort": { "field": "added_at", "order": "desc" }, "limit": 10000000 },
          "sort_config": {} }
        """;

    [Fact]
    public async Task RulesAsSaved_AreNotChanged_UntilTheViewerChangesThem()
    {
        _harness.Silo.On(HttpMethod.Get, CollectionPath, Versioned(Awkward, "\"rev-1\""));
        _harness.Silo.On(HttpMethod.Patch, CollectionPath, Versioned(Awkward, "\"rev-2\""));
        var editor = await EditAsync();

        Assert.Empty(editor.ChangedFields);
        Assert.False(editor.ShowsSaveBar);
        Assert.Equal([false, false, true, true, true], editor.Rules.Groups[0].Rules.Select(r => r.IsLocked));
        Assert.Equal("1.50", Rule(editor, 1).Text);

        editor.Name = "Odd";
        await editor.SaveCommand.ExecuteAsync(null);

        var rules = Sent(HttpMethod.Patch, CollectionPath).GetProperty("query_definition").GetProperty("groups")[0].GetProperty("rules");
        Assert.Equal(["\"1990\"", "1.50", "[\"Comedy\",\"Drama\"]", "\"anime\"", "\"12h\""], rules.EnumerateArray().Select(r => r.GetProperty("value").GetRawText()));
    }

    [Fact]
    public async Task ATypedLimitOfNone_IsNone()
    {
        var editor = await EditAsync();

        editor.Rules.LimitText = "10000000";

        Assert.Null(editor.Rules.ToQuery().Limit);
        Assert.Empty(editor.ChangedFields);
    }

    [Fact]
    public async Task RulesChangedAndBack_AskForNoPreview()
    {
        var editor = await EditAsync();
        int Previews() => _harness.Silo.SentTo(HttpMethod.Post, PreviewPath).Count;
        int before = Previews();

        Rule(editor, 0).Text = "Drama";
        Rule(editor, 0).Text = "Comedy";
        _harness.Clock.Advance(CollectionEditorViewModel.PreviewDelay);
        await editor.Previewing;

        Assert.Equal(before, Previews());
        Assert.False(editor.IsPreviewRefreshing);
    }

    [Fact]
    public async Task Discard_PreviewsTheRulesPutBack()
    {
        var editor = await EditAsync();
        Rule(editor, 0).Text = "Drama";
        _harness.Clock.Advance(CollectionEditorViewModel.PreviewDelay);
        await editor.Previewing;

        editor.DiscardCommand.Execute(null);
        _harness.Clock.Advance(CollectionEditorViewModel.PreviewDelay);
        await editor.Previewing;

        Assert.Contains("Comedy", Sent(HttpMethod.Post, PreviewPath).GetProperty("query_definition").GetRawText(), StringComparison.Ordinal);
        Assert.Empty(editor.ChangedFields);
    }

    [Fact]
    public async Task AnEmptyNumber_HoldsThePreviewBack()
    {
        var editor = await EditAsync();
        int before = _harness.Silo.SentTo(HttpMethod.Post, PreviewPath).Count;

        Rule(editor, 1).From = "";
        _harness.Clock.Advance(CollectionEditorViewModel.PreviewDelay);
        await editor.Previewing;

        Assert.True(editor.Rules.HasIncompleteRule);
        Assert.Equal(before, _harness.Silo.SentTo(HttpMethod.Post, PreviewPath).Count);
        Assert.True(editor.HasPreviewTiles);
        Assert.Equal(["Rules"], editor.ChangedFields);
    }

    [Fact]
    public async Task APreviewThatThrows_IsLogged_AndSaysSo()
    {
        _harness.Silo.On(HttpMethod.Post, PreviewPath, () => throw new InvalidOperationException("boom"));

        var editor = await EditAsync();

        Assert.True(editor.HasPreviewFailed);
    }

    [Fact]
    public async Task AnotherVisit_StartsWithNoRulesOrPreview()
    {
        var editor = await EditAsync();
        _harness.Silo.On(HttpMethod.Post, PreviewPath, ScriptedSilo.Problem(503, "dependency_unavailable"));

        await editor.ActivateAsync(null, PersonalCollectionTypes.Smart);
        await editor.Previewing;

        Assert.True(editor.Rules.HasNoRules);
        Assert.Empty(editor.PreviewTiles);
        Assert.Equal("", editor.PreviewCount);
        Assert.All(editor.Rules.Libraries, l => Assert.False(l.IsPicked));
    }

    [Fact]
    public async Task TheLibrariesArriving_LeaveARuleBeingTypedAlone()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _harness.Answer("api/v2/user/libraries", LibraryHarness.Held(release, LibraryPayloads.Libraries));
        var editor = Create();
        var activation = editor.ActivateAsync("s1");
        for (int i = 0; i < 200 && editor.Rules.Groups.Count == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        var rule = Rule(editor, 0);
        rule.Text = "Dram";

        release.SetResult();
        await activation;

        Assert.Same(rule, Rule(editor, 0));
        Assert.Equal("Dram", rule.Text);
        Assert.Equal("Filme", editor.Rules.LibrariesText);
    }

    [Fact]
    public async Task AnotherKindOfTitles_TakesItsFirstSort_WhenTheSortDoesNotFit()
    {
        var editor = await EditAsync();
        editor.Rules.SortField = editor.Rules.Sorts.First(s => s.Value == "rating_imdb");

        editor.Rules.Scope = CollectionRuleFields.Scopes.First(s => s.Value == "audiobook");

        Assert.Equal(new SmartSort("title", "asc"), editor.Rules.ToQuery().Sort);
        Assert.DoesNotContain(editor.Rules.Sorts, s => s.Value == "rating_imdb");
    }

    [Fact]
    public void RulesKey_IgnoresTheOrderOfARulesMembers()
    {
        var a = SmartQuery.Read(JsonDocument.Parse("""{ "groups": [ { "rules": [ { "field": "genre", "op": "is", "value": "x", "a": 1, "b": 2 } ] } ] }""").RootElement);
        var b = SmartQuery.Read(JsonDocument.Parse("""{ "groups": [ { "rules": [ { "b": 2, "value": "x", "a": 1, "op": "is", "field": "genre" } ] } ] }""").RootElement);

        Assert.Equal(a.RulesKey(), b.RulesKey());
        Assert.Equal(24, SmartQuery.Read(JsonDocument.Parse("""{ "limit": 24.0 }""").RootElement).Limit);
    }

    [Fact]
    public void Rules_AreReadAsTheWebNormalizesThem()
    {
        var query = SmartQuery.Read(JsonDocument.Parse("""
            { "library_ids": [2, 2, -1, 1.0, "3"], "media_scope": "podcasts", "match": "weird",
              "groups": [ { "match": "any", "rules": [ { "field": "rating", "op": "gte", "value": 7.5 } ] } ],
              "sort": { "field": "recently_added" }, "limit": 0 }
            """).RootElement);

        Assert.Equal([2L, 1L], query.LibraryIds);
        Assert.Null(query.MediaScope);
        Assert.Equal("all", query.Match);
        Assert.Equal("rating_imdb", query.Groups[0].Rules[0].Field);
        Assert.Equal(new SmartSort("added_at", "desc"), query.Sort);
        Assert.Null(query.Limit);
        Assert.Equal(query.RulesKey(), (query with { LibraryIds = [9] }).RulesKey());
        Assert.Equal("""{"x":1}""", SmartSortConfig.ClearDefaultSort(JsonDocument.Parse("""{ "field": "year", "order": "asc", "x": 1 }""").RootElement).GetRawText());
    }

    [Fact]
    public void Merge_TakesTheirLibraries_AndKeepsMyRules()
    {
        var original = CollectionDraft.EmptySmart with { Name = "Comedies" };
        var rule = new SmartRuleGroup("all", [new SmartRule { Field = "genre", Op = "is", Value = SmartValue.Text("Comedy") }]);
        var mine = original with { Rules = original.Rules! with { Groups = [rule] } };
        var theirs = original with { Rules = original.Rules! with { LibraryIds = [2] } };

        var (merged, conflicts) = CollectionDraft.Merge(original, mine, theirs);

        Assert.Empty(conflicts);
        Assert.Equal([2L], merged.Rules!.LibraryIds);
        Assert.Single(merged.Rules.Groups);

        var (_, both) = CollectionDraft.Merge(original, mine, theirs with { Rules = theirs.Rules! with { MediaScope = "movie" } });
        Assert.Equal(["Rules"], both);
    }
}
