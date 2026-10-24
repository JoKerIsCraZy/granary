using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

public sealed class PlayerViewModelTests
{
    private readonly FakePlaybackEngine _engine = new();

    private PlayerViewModel CreateViewModel() => new(_engine, new ImmediateDispatcher());

    [Fact]
    public void Open_LoadsAnHttpUrlAsIs()
    {
        var viewModel = CreateViewModel();
        viewModel.Source = " https://nas.example/api/v2/stream/abc ";

        viewModel.OpenCommand.Execute(null);

        var source = Assert.Single(_engine.Loaded);
        Assert.Equal("https://nas.example/api/v2/stream/abc", source.Url.AbsoluteUri);
        Assert.Empty(source.Headers);
    }

    [Fact]
    public void Open_TreatsAnythingElseAsALocalPath()
    {
        var viewModel = CreateViewModel();
        viewModel.Source = "\"C:\\Videos\\movie.mkv\"";

        viewModel.OpenCommand.Execute(null);

        var source = Assert.Single(_engine.Loaded);
        Assert.True(source.Url.IsFile);
        Assert.Equal(@"C:\Videos\movie.mkv", source.Url.LocalPath);
    }

    [Fact]
    public void OpenCommand_IsDisabledWithoutASource()
    {
        var viewModel = CreateViewModel();

        Assert.False(viewModel.OpenCommand.CanExecute(null));
    }

    [Fact]
    public void StateChanges_UpdateTheTransportProperties()
    {
        var viewModel = CreateViewModel();

        _engine.RaiseState(new PlaybackSnapshot(true, true, TimeSpan.FromSeconds(12.5), TimeSpan.FromSeconds(30), "d3d11va"));

        Assert.True(viewModel.IsPaused);
        Assert.Equal(12.5, viewModel.PositionSeconds);
        Assert.Equal(30, viewModel.DurationSeconds);
        Assert.Contains("d3d11va", viewModel.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PlaybackEndReason.EndOfFile, "Finished")]
    [InlineData(PlaybackEndReason.AuthenticationFailed, "rejected the credentials")]
    [InlineData(PlaybackEndReason.Error, "Playback failed")]
    public void Ended_SetsAStatus(PlaybackEndReason reason, string expected)
    {
        var viewModel = CreateViewModel();

        _engine.RaiseEnded(new PlaybackEnded(reason, "loading failed"));

        Assert.Contains(expected, viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void TogglePauseAndSeek_DriveTheEngine()
    {
        var viewModel = CreateViewModel();

        viewModel.TogglePauseCommand.Execute(null);
        viewModel.SeekTo(42);
        viewModel.SeekTo(-3);

        Assert.True(_engine.Current.IsPaused);
        Assert.Equal([TimeSpan.FromSeconds(42), TimeSpan.Zero], _engine.Seeks);
    }

    [Fact]
    public void Dispose_UnsubscribesFromTheEngine()
    {
        var viewModel = CreateViewModel();

        viewModel.Dispose();

        Assert.False(_engine.HasSubscribers);
    }
}
