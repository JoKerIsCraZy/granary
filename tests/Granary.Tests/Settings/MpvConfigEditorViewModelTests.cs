using Granary.Core.Playback;
using Granary.Core.ViewModels.Settings;
using Granary.Player;
using Granary.Tests.Fakes;

namespace Granary.Tests.Settings;

/// <summary>Settings → Playback → This PC → Edit: mpv.conf edited inside Granary.</summary>
public sealed class MpvConfigEditorViewModelTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeConfirmation _confirmation = new();
    private readonly MpvConfigChanges _changes = new();
    private readonly MpvConfigFolder _folder;

    public MpvConfigEditorViewModelTests() => _folder = new(Path.Combine(_data, "mpv"), new LogSink().For<MpvConfigFolder>());

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private MpvConfigEditorViewModel Editor() => new(_folder, new MpvConfigCheck(new MpvEngineOptions()), _changes, _confirmation);

    private async Task<MpvConfigEditorViewModel> OpenWith(string? file)
    {
        if (file is not null)
        {
            Directory.CreateDirectory(_folder.Directory);
            await File.WriteAllTextAsync(_folder.ConfigFile, file, TestContext.Current.CancellationToken);
        }
        var editor = Editor();
        await editor.LoadAsync();
        return editor;
    }

    private Task<string> OnDisk() => File.ReadAllTextAsync(_folder.ConfigFile, TestContext.Current.CancellationToken);

    [Fact]
    public async Task TheEditor_HoldsTheFilesExactText()
    {
        const string file = "# mine\r\ndeband=yes\r\n\r\n[hq]\r\nprofile-cond=p[\"video-params/h\"] >= 2160\r\ninclude=~~/more.conf\r\n";

        var editor = await OpenWith(file);

        Assert.Equal(file, editor.Text);
        Assert.True(editor.CanEdit);
        Assert.False(editor.IsLoading);
        Assert.False(editor.HasUnsavedChanges);
        Assert.Equal("", editor.Message);
    }

    [Fact]
    public async Task AMissingFile_OpensWithTheTemplate_AndSaveMakesIt()
    {
        var editor = await OpenWith(file: null);

        Assert.Equal(MpvConfigFolder.Template.ReplaceLineEndings("\r\n"), editor.Text);
        Assert.False(File.Exists(_folder.ConfigFile));

        Assert.True(await editor.SaveAsync());

        Assert.Equal(editor.Text, await OnDisk());
        Assert.True(_changes.IsPending);
    }

    [Fact]
    public async Task Save_WritesTheTextAsItIs_AndTheChangeIsPending()
    {
        var editor = await OpenWith("deband=yes\r\n");
        editor.Text = "deband=no\r\n# kept as written\r\n[hq]\r\nscale=ewa_lanczossharp\r\n";
        Assert.True(editor.HasUnsavedChanges);

        Assert.True(await editor.SaveAsync());

        Assert.Equal("deband=no\r\n# kept as written\r\n[hq]\r\nscale=ewa_lanczossharp\r\n", await OnDisk());
        Assert.False(editor.HasUnsavedChanges);
        Assert.True(_changes.IsPending);
        Assert.Equal(["mpv.conf"], Directory.GetFiles(_folder.Directory).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("deband=yes\n", "deband=yes\ndeband-iterations=2\n")]
    [InlineData("deband=yes\r\n", "deband=yes\r\ndeband-iterations=2\r\n")]
    public async Task TheTextBoxsLineBreaks_AreSavedAsTheFileHadThem(string file, string saved)
    {
        var editor = await OpenWith(file);

        // A text box hands its lines back with a lone carriage return between them.
        editor.Text = "deband=yes\rdeband-iterations=2\r";
        Assert.True(await editor.SaveAsync());

        Assert.Equal(saved, await OnDisk());
    }

    [Fact]
    public async Task LineBreaksAlone_AreNoChange()
    {
        var editor = await OpenWith("deband=yes\r\nscale=spline36\r\n");

        editor.Text = "deband=yes\rscale=spline36\r";

        Assert.False(editor.HasUnsavedChanges);
    }

    [Fact]
    public async Task SaveWithoutChanges_LeavesTheFile_AndNothingIsPending()
    {
        var editor = await OpenWith("deband=yes\n");

        Assert.True(await editor.SaveAsync());

        Assert.False(_changes.IsPending);
    }

    [Fact]
    public async Task TheCheck_FollowsTheText()
    {
        var editor = await OpenWith("deband=yes\n");
        Assert.Equal([MpvConfigEditorViewModel.NothingToNote], editor.Notes);

        editor.Text = "vo=gpu\nkeep-open=no\n";

        Assert.Equal(["Granary keeps its own vo.", "Granary keeps its own keep-open."], editor.Notes);
    }

    [Fact]
    public async Task AFileThePlayerWouldNotUse_IsSaid_ButStillSaved()
    {
        var editor = await OpenWith("deband=yes\n");

        editor.Text = "player-operation-mode=cplayer\n";

        Assert.StartsWith("This file would not be used: it sets player-operation-mode", editor.Notes[0], StringComparison.Ordinal);
        Assert.True(await editor.SaveAsync());
        Assert.Equal("player-operation-mode=cplayer\n", await OnDisk());
    }

    [Fact]
    public async Task ASaveThatFails_SaysSo_AndKeepsTheEdits()
    {
        var editor = await OpenWith("deband=yes\n");
        // A folder where the file is written first.
        Directory.CreateDirectory(_folder.ConfigFile + ".tmp");
        editor.Text = "deband=no\n";

        Assert.False(await editor.SaveAsync());

        Assert.Equal($"Couldn't save mpv.conf in {_folder.Directory}.", editor.Message);
        Assert.True(editor.HasUnsavedChanges);
        Assert.False(_changes.IsPending);
        Assert.Equal("deband=yes\n", await OnDisk());

        // The next try that works clears it.
        Directory.Delete(_folder.ConfigFile + ".tmp");
        Assert.True(await editor.SaveAsync());
        Assert.Equal("", editor.Message);
    }

    [Fact]
    public async Task AFolderInMpvConfsPlace_CannotBeEdited()
    {
        Directory.CreateDirectory(_folder.ConfigFile);

        var editor = await OpenWith(file: null);

        Assert.False(editor.CanEdit);
        Assert.Equal($"mpv.conf in {_folder.Directory} is a folder. Rename or remove it, then try again.", editor.Message);
        Assert.False(await editor.SaveAsync());
    }

    [Fact]
    public async Task Cancel_WithoutChanges_AsksNothing()
    {
        var editor = await OpenWith("deband=yes\n");

        Assert.True(await editor.ConfirmDiscardAsync());

        Assert.Empty(_confirmation.Asked);
    }

    [Fact]
    public async Task Cancel_WithChanges_AsksFirst_AndKeepsThemOnCancel()
    {
        var editor = await OpenWith("deband=yes\n");
        editor.Text = "deband=no\n";
        _confirmation.Answer = false;

        Assert.False(await editor.ConfirmDiscardAsync());

        Assert.Equal([("Discard unsaved changes?", "Your edits to mpv.conf were never saved. Leaving now throws them away.", "Discard")],
            _confirmation.Asked);
        Assert.Equal("deband=no\n", editor.Text);
        Assert.True(editor.HasUnsavedChanges);
    }

    [Fact]
    public async Task Cancel_WithChanges_ThrowsThemAway_WhenTheViewerSaysSo()
    {
        var editor = await OpenWith("deband=yes\n");
        editor.Text = "deband=no\n";

        Assert.True(await editor.ConfirmDiscardAsync());

        Assert.Equal("deband=yes\n", await OnDisk());
        Assert.False(_changes.IsPending);
    }
}
