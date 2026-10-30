using Granary.Core.Api.Catalog;
using Granary.Core.Trailers;
using Granary.Core.ViewModels.Detail;
using Granary.Tests.Fakes;

namespace Granary.Tests.Trailers;

public sealed class TrailerOverlayViewModelTests
{
    private static readonly DetailTrailer Official = DetailTrailer.From(new ItemVideo("Trailer", "YouTube", "xEQP4VVuyrY", true, "Official Trailer", "en"))!;
    private static readonly DetailTrailer Teaser = DetailTrailer.From(new ItemVideo("Teaser", "YouTube", "aaaaaaaaaaa", false, "Teaser", "en"))!;

    private static readonly TrailerPageMessage Ready = new(TrailerPageEvent.Ready, 0);
    private static readonly TrailerPageMessage Playing = new(TrailerPageEvent.Playing, 0);

    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();

    private TrailerOverlayViewModel Create(Granary.Core.Abstractions.IUiDispatcher? dispatcher = null) =>
        new(dispatcher ?? new ImmediateDispatcher(), _time, _logs.For<TrailerOverlayViewModel>());

    private static TrailerPageMessage Error(int code) => new(TrailerPageEvent.Error, code);

    [Fact]
    public void ItStartsClosed()
    {
        var overlay = Create();

        Assert.Equal(TrailerOverlayState.Closed, overlay.State);
        Assert.False(overlay.IsOpen);
        Assert.Null(overlay.Trailer);
    }

    [Fact]
    public void ATrailer_OpensLoading_WithItsTitle()
    {
        var overlay = Create();

        overlay.Open(Official);

        Assert.Equal(TrailerOverlayState.Loading, overlay.State);
        Assert.True(overlay.IsOpen);
        Assert.True(overlay.IsLoading);
        Assert.Equal("Official Trailer", overlay.Title);
        Assert.Equal("Trailer · Official", overlay.Caption);
        Assert.Equal(TrailerFailure.None, overlay.Failure);
    }

    [Fact]
    public void Opening_IsAnnounced_SoThePlayerBarCanPause()
    {
        var overlay = Create();
        int opened = 0;
        overlay.Opened += (_, _) => opened++;

        overlay.Open(Official);

        Assert.Equal(1, opened);
    }

    [Fact]
    public void ThePlayersAnswer_ShowsTheTrailer()
    {
        var overlay = Create();
        overlay.Open(Official);

        overlay.Receive(Ready);

        Assert.Equal(TrailerOverlayState.Playing, overlay.State);
        Assert.True(overlay.IsPlaying);
    }

    [Fact]
    public void PlaybackStarting_ShowsTheTrailerToo()
    {
        var overlay = Create();
        overlay.Open(Official);

        overlay.Receive(Playing);

        Assert.Equal(TrailerOverlayState.Playing, overlay.State);
    }

    [Theory]
    [InlineData(2, TrailerFailure.PlayerError)]
    [InlineData(5, TrailerFailure.PlayerError)]
    [InlineData(100, TrailerFailure.NotFound)]
    [InlineData(101, TrailerFailure.EmbedRefused)]
    [InlineData(150, TrailerFailure.EmbedRefused)]
    [InlineData(153, TrailerFailure.Configuration)]
    [InlineData(999, TrailerFailure.PlayerError)]
    public void APlayerError_LeavesTheTrailerToYouTube(int code, TrailerFailure failure)
    {
        var overlay = Create();
        overlay.Open(Official);

        overlay.Receive(Error(code));

        Assert.Equal(TrailerOverlayState.Unavailable, overlay.State);
        Assert.True(overlay.IsUnavailable);
        Assert.Equal(failure, overlay.Failure);
        Assert.Equal("This video can't play in Granary.", TrailerOverlayViewModel.UnavailableText);
        Assert.Equal(new Uri("https://www.youtube.com/watch?v=xEQP4VVuyrY"), overlay.WatchUrl);
    }

    [Fact]
    public void APlayerError_AfterItStarted_EndsItToo()
    {
        var overlay = Create();
        overlay.Open(Official);
        overlay.Receive(Ready);

        overlay.Receive(Error(150));

        Assert.Equal(TrailerOverlayState.Unavailable, overlay.State);
    }

    [Fact]
    public void NoAnswerInTime_MakesItUnavailable()
    {
        var overlay = Create();
        overlay.Open(Official);

        _time.Advance(TrailerOverlayViewModel.LoadTimeout - TimeSpan.FromMilliseconds(1));
        Assert.Equal(TrailerOverlayState.Loading, overlay.State);

        _time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(TrailerOverlayState.Unavailable, overlay.State);
        Assert.Equal(TrailerFailure.TimedOut, overlay.Failure);
    }

    [Fact]
    public void AnAnswer_StopsTheClock()
    {
        var overlay = Create();
        overlay.Open(Official);
        overlay.Receive(Ready);

        _time.Advance(TrailerOverlayViewModel.LoadTimeout * 2);

        Assert.Equal(TrailerOverlayState.Playing, overlay.State);
        Assert.Null(_time.NextDue);
    }

    [Theory]
    [InlineData(TrailerFailure.NoRuntime)]
    [InlineData(TrailerFailure.BrowserFailed)]
    public void ABrowserThatCannotRun_MakesItUnavailable(TrailerFailure failure)
    {
        var overlay = Create();
        overlay.Open(Official);
        overlay.Receive(Ready);

        overlay.Fail(failure);

        Assert.Equal(TrailerOverlayState.Unavailable, overlay.State);
        Assert.Equal(failure, overlay.Failure);
        Assert.Null(_time.NextDue);
    }

    [Fact]
    public void AnUnavailableTrailer_StaysUnavailable_WhateverThePageSaysLater()
    {
        var overlay = Create();
        overlay.Open(Official);
        overlay.Receive(Error(101));

        overlay.Receive(Ready);
        overlay.Receive(Error(5));
        overlay.Fail(TrailerFailure.BrowserFailed);

        Assert.Equal(TrailerOverlayState.Unavailable, overlay.State);
        Assert.Equal(TrailerFailure.EmbedRefused, overlay.Failure);
    }

    [Fact]
    public void Close_EndsIt_AndNothingLaterOpensItAgain()
    {
        var overlay = Create();
        overlay.Open(Official);

        overlay.CloseCommand.Execute(null);
        overlay.Receive(Ready);
        overlay.Fail(TrailerFailure.BrowserFailed);
        _time.Advance(TrailerOverlayViewModel.LoadTimeout * 2);

        Assert.Equal(TrailerOverlayState.Closed, overlay.State);
        Assert.False(overlay.IsOpen);
        Assert.Null(overlay.Trailer);
        Assert.Equal(TrailerFailure.None, overlay.Failure);
    }

    [Fact]
    public void AnotherTrailer_StartsOver()
    {
        var overlay = Create();
        overlay.Open(Official);
        overlay.Receive(Error(150));

        overlay.Open(Teaser);

        Assert.Equal(TrailerOverlayState.Loading, overlay.State);
        Assert.Equal(TrailerFailure.None, overlay.Failure);
        Assert.Equal("Teaser", overlay.Title);
    }

    [Fact]
    public void AnEarlierTrailersTimeout_DoesNotEndTheNextOne()
    {
        // The timeout fired as the first trailer closed; the UI thread runs it only after the next one opened.
        var dispatcher = new QueuedDispatcher();
        var overlay = Create(dispatcher);
        overlay.Open(Official);
        _time.Advance(TrailerOverlayViewModel.LoadTimeout);
        overlay.Close();
        overlay.Open(Teaser);

        dispatcher.RunAll();

        Assert.Equal(TrailerOverlayState.Loading, overlay.State);
    }

    [Fact]
    public void TheLog_NamesTheOutcome_NeverTheVideo()
    {
        var overlay = Create();
        overlay.Open(Official);
        overlay.Receive(Ready);
        overlay.Receive(Error(150));
        overlay.Open(Teaser);
        _time.Advance(TrailerOverlayViewModel.LoadTimeout);

        Assert.Contains(_logs.Lines, line => line.Contains("EmbedRefused", StringComparison.Ordinal));
        Assert.Contains(_logs.Lines, line => line.Contains("TimedOut", StringComparison.Ordinal));
        Assert.DoesNotContain("xEQP4VVuyrY", _logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain("aaaaaaaaaaa", _logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain("youtube", _logs.All, StringComparison.OrdinalIgnoreCase);
    }
}
