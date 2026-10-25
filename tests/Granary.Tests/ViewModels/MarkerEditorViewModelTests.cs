using Granary.Core.Api;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Player;

namespace Granary.Tests.ViewModels;

/// <summary>The player's marker editor, as the web's useMarkerEditor drives it.</summary>
public sealed class MarkerEditorViewModelTests
{
    private readonly List<IReadOnlyDictionary<string, WatchMarker?>> _saves = [];
    private Func<Task> _answer = () => Task.CompletedTask;

    private MarkerEditorViewModel Editor() => new(changes =>
    {
        _saves.Add(changes);
        return _answer();
    });

    private static readonly IReadOnlyList<PlaybackMarker> Saved =
    [
        new(WatchMarkerKinds.Intro, 3, 12),
        new(WatchMarkerKinds.Credits, 1200, 1300),
        new(WatchMarkerKinds.Credits, 1400, 1500),
    ];

    [Fact]
    public void Begin_TakesTheFirstOfEachKind_OnTheFirstKindThatHasOne()
    {
        var editor = Editor();

        editor.Begin(Saved, 1500);

        Assert.True(editor.IsEditing);
        Assert.Equal(WatchMarkerKinds.Intro, editor.ActiveKind);
        Assert.Equal(["0:03 – 0:12", "Not set", "20:00 – 21:40", "Not set"], editor.Rows.Select(r => r.RangeText));
        Assert.Equal(["Intro", "Recap", "Credits / Outro", "Preview"], editor.Rows.Select(r => r.Label));
        Assert.False(editor.IsDirty);
        Assert.Equal([3.0, 1200.0], editor.DraftMarkers.Select(m => m.StartSeconds));
    }

    [Fact]
    public void ANewPart_GetsAMinuteOnTheOtherSide_WithinTheTitle()
    {
        var editor = Editor();
        editor.Begin(Saved, 1500);

        editor.SetEdge(WatchMarkerKinds.Recap, MarkerEdge.Start, 30);
        editor.SetEdge(WatchMarkerKinds.Preview, MarkerEdge.End, 20);
        editor.SetEdge(WatchMarkerKinds.Credits, MarkerEdge.Start, 1480);

        Assert.Equal("0:30 – 1:30", editor.Rows[1].RangeText);
        Assert.Equal("0:00 – 0:20", editor.Rows[3].RangeText);
        Assert.Equal(WatchMarkerKinds.Credits, editor.ActiveKind);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public void AnEnd_NeverPassesTheOther()
    {
        var editor = Editor();
        editor.Begin(Saved, 1500);

        editor.SetEdge(WatchMarkerKinds.Intro, MarkerEdge.Start, 20);
        var intro = editor.DraftMarkers.Single(m => m.Kind == WatchMarkerKinds.Intro);
        Assert.Equal((11.5, 12.0), (intro.StartSeconds, intro.EndSeconds));

        editor.SetEdge(WatchMarkerKinds.Intro, MarkerEdge.End, 0);
        intro = editor.DraftMarkers.Single(m => m.Kind == WatchMarkerKinds.Intro);
        Assert.Equal((11.5, 12.0), (intro.StartSeconds, intro.EndSeconds));
    }

    [Fact]
    public async Task Save_SendsOnlyTheKindsThatChanged_AClearedOneAsNull()
    {
        var editor = Editor();
        editor.Begin(Saved, 1500);
        editor.SetEdge(WatchMarkerKinds.Intro, MarkerEdge.End, 15);
        editor.Clear(WatchMarkerKinds.Credits);
        editor.SetEdge(WatchMarkerKinds.Recap, MarkerEdge.Start, 40);
        editor.Reset(WatchMarkerKinds.Recap);

        await editor.SaveCommand.ExecuteAsync(null);

        var changes = Assert.Single(_saves);
        Assert.Equal([WatchMarkerKinds.Intro, WatchMarkerKinds.Credits], changes.Keys);
        Assert.Equal(new WatchMarker(3, 15), changes[WatchMarkerKinds.Intro]);
        Assert.Null(changes[WatchMarkerKinds.Credits]);
        Assert.False(editor.IsEditing);
    }

    [Fact]
    public void Save_WaitsForAChange()
    {
        var editor = Editor();
        editor.Begin(Saved, 1500);

        Assert.False(editor.SaveCommand.CanExecute(null));

        editor.SetEdge(WatchMarkerKinds.Intro, MarkerEdge.End, 15);
        Assert.True(editor.SaveCommand.CanExecute(null));

        editor.ResetAllCommand.Execute(null);
        Assert.False(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task ARefusedSave_SaysWhy_AndKeepsTheDraft()
    {
        _answer = () => Task.FromException(new SiloApiException(403, new ApiProblem(ApiProblem.TypeOrigin + "forbidden", "forbidden", 403, null, null, null)));
        var editor = Editor();
        editor.Begin(Saved, 1500);
        editor.SetEdge(WatchMarkerKinds.Intro, MarkerEdge.End, 15);

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal("This account may not edit markers.", editor.Error);
        Assert.True(editor.IsEditing);
        Assert.True(editor.IsDirty);
        Assert.False(editor.IsSaving);
    }

    [Fact]
    public void Cancel_DropsTheDraft_AndTheNextBeginStartsFromTheSavedMarkers()
    {
        var editor = Editor();
        editor.Begin(Saved, 1500);
        editor.Clear(WatchMarkerKinds.Intro);

        editor.CancelCommand.Execute(null);
        editor.Begin(Saved, 1500);

        Assert.Equal("0:03 – 0:12", editor.Rows[0].RangeText);
        Assert.False(editor.IsDirty);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(187.9, "3:07")]
    [InlineData(3725, "1:02:05")]
    public void Times_AreWrittenAsOnTheWeb(double seconds, string text) => Assert.Equal(text, MarkerEditorViewModel.Time(seconds));
}
