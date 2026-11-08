using Granary.Core.Api.Catalog;
using Granary.Core.Playback.Themes;
using Granary.Tests.Fakes;

namespace Granary.Tests.Playback;

/// <summary>One theme at a time, as the web's lib/themeMusic.ts and the apps' players built on it play it.</summary>
public sealed class ThemeMusicPlayerTests
{
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(300);

    private readonly ManualTimeProvider _clock = new();
    private readonly FakeThemeAudioOutput _output = new();
    private readonly List<(string Owner, string Theme)> _fetched = [];
    private readonly ThemeMusicPlayer _player;

    private readonly LogSink _logs = new();

    public ThemeMusicPlayerTests() =>
        _player = new ThemeMusicPlayer(FetchAsync, _output, new ImmediateDispatcher(), _clock, _logs.For<ThemeMusicPlayer>());

    /// <summary>What the next fetches answer, in order; the last repeats. Null is a refused grant.</summary>
    private Queue<Func<Task<ThemeAudioSource?>>> Answers { get; } = new();

    private Func<Task<ThemeAudioSource?>> _last = () => Task.FromResult<ThemeAudioSource?>(Original);

    private static ThemeAudioSource Original => new(new byte[] { 1, 2, 3 }, "audio/mpeg", Converted: false);

    private static ThemeAudioSource Converted => new(new byte[] { 4, 5 }, "audio/mp4", Converted: true);

    private Task<ThemeAudioSource?> FetchAsync(string owner, string theme, CancellationToken cancellationToken)
    {
        _fetched.Add((owner, theme));
        if (Answers.TryDequeue(out var next))
        {
            _last = next;
        }
        return _last();
    }

    private void Answer(ThemeAudioSource? source) => Answers.Enqueue(() => Task.FromResult(source));

    internal static ThemeSongSet Set(string owner, params string[] themes) =>
        new() { OwnerId = owner, Items = [.. themes.Select(id => new ThemeSong { Id = id, Title = "Theme " + id })] };

    private FakeThemeAudio Playing()
    {
        var audio = Assert.Single(_output.Alive);
        Assert.True(audio.IsPlaying);
        return audio;
    }

    [Fact]
    public void ASelection_PlaysItsFirstTheme_SilentUntilItIsHeard()
    {
        _player.Select(Set("movie-1", "11", "12"), loop: false);

        Assert.Equal([("movie-1", "11")], _fetched);
        var audio = Playing();
        Assert.Equal(0, audio.Volume);
        Assert.Equal("audio/mpeg", audio.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3 }, audio.Audio);
        Assert.True(_player.IsAudible);
    }

    [Fact]
    public void OnceHeard_ItFadesInTo35Percent_In12StepsOver300Ms()
    {
        _player.Select(Set("movie-1", "11"), loop: false);
        var audio = Playing();

        audio.Start();
        _clock.Advance(TimeSpan.FromMilliseconds(150));
        Assert.Equal(0.175, audio.Volume, 6);
        _clock.Advance(TimeSpan.FromMilliseconds(150));

        Assert.Equal(ThemeMusicPlayer.Volume, audio.Volume, 6);
        Assert.Null(_clock.NextDue);
    }

    [Fact]
    public void TheSameThemeAgain_GoesOn()
    {
        _player.Select(Set("movie-1", "11"), loop: false);
        var audio = Playing();

        _player.Select(Set("movie-1", "11"), loop: false);

        Assert.Single(_fetched);
        Assert.Same(audio, Playing());
        Assert.Equal(1, audio.Plays);
    }

    [Fact]
    public void Suspended_ItFadesOutAndPauses_ThenTheSameThemeGoesOnWhereItWas()
    {
        _player.Select(Set("series-1", "21"), loop: false);
        var audio = Playing();
        audio.Start();
        _clock.Advance(Fade);

        _player.Suspend();
        _clock.Advance(Fade);
        Assert.Equal(0, audio.Volume, 6);
        Assert.False(audio.IsPlaying);
        Assert.False(_player.IsAudible);

        _player.Select(Set("series-1", "21"), loop: false);
        _clock.Advance(Fade);

        Assert.Same(audio, Playing());
        Assert.Equal(ThemeMusicPlayer.Volume, audio.Volume, 6);
        Assert.Single(_fetched);
    }

    [Fact]
    public void AnotherTheme_FadesTheFirstOut_WhileTheNextOneComes()
    {
        _player.Select(Set("movie-1", "11"), loop: false);
        var first = Playing();
        first.Start();
        _clock.Advance(Fade);

        _player.Select(Set("movie-2", "31"), loop: false);

        Assert.Equal([("movie-1", "11"), ("movie-2", "31")], _fetched);
        Assert.False(first.IsDisposed);
        _clock.Advance(Fade);
        Assert.True(first.IsDisposed);
        Assert.Equal(0, first.Volume, 6);
        var second = Playing();
        Assert.NotSame(first, second);
    }

    [Fact]
    public void AStopAtOnce_SilencesTheThemeAndOneStillFadingOut()
    {
        _player.Select(Set("movie-1", "11"), loop: false);
        var first = Playing();
        first.Start();
        _clock.Advance(Fade);
        _player.Select(Set("movie-2", "31"), loop: false);
        var second = _output.Last!;

        _player.Stop(immediate: true);

        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
        Assert.False(_player.IsAudible);
        Assert.Null(_clock.NextDue);
    }

    [Fact]
    public void AnOriginal_LoopsInPlace_AConversionDoesNot()
    {
        _player.Select(Set("movie-1", "11"), loop: true);
        Assert.True(Playing().IsLooping);

        Answer(Converted);
        _player.Select(Set("movie-2", "31"), loop: true);

        Assert.False(_output.Last!.IsLooping);
    }

    [Fact]
    public void TurningTheLoopOn_LoopsTheThemeThatPlays()
    {
        _player.Select(Set("movie-1", "11"), loop: false);
        var audio = Playing();

        _player.Select(Set("movie-1", "11"), loop: true);

        Assert.True(audio.IsLooping);
        Assert.Single(_fetched);
    }

    [Fact]
    public void ALoopedConversion_PlaysAgainFromANewGrant_WhenItEnds()
    {
        Answer(Converted);
        _player.Select(Set("movie-1", "11"), loop: true);
        var first = Playing();
        first.Start();

        first.End();

        Assert.Equal(2, _fetched.Count);
        Assert.True(first.IsDisposed);
        Assert.NotSame(first, Playing());
    }

    [Fact]
    public void AConversionThatEndsWithoutPlaying_IsTriedOnceMore_ThenLeftSilent()
    {
        Answer(Converted);
        _player.Select(Set("movie-1", "11"), loop: true);

        _output.Last!.End();
        _output.Last!.End();

        Assert.Equal(2, _fetched.Count);
        Assert.Empty(_output.Alive);
    }

    [Fact]
    public void WithoutTheLoop_AThemeThatEnded_DoesNotStartOver_WhenItsPageShowsAgain()
    {
        _player.Select(Set("movie-1", "11"), loop: false);
        var audio = Playing();
        audio.Start();

        audio.End();
        _player.Suspend();
        _player.Select(Set("movie-1", "11"), loop: false);

        Assert.Single(_fetched);
        Assert.Empty(_output.Alive);
    }

    [Fact]
    public void AFailure_IsTriedOnceWithANewGrant_AndPlayingEarnsTheTryBack()
    {
        _player.Select(Set("movie-1", "11"), loop: false);

        _output.Last!.Fail();
        Assert.Equal(2, _fetched.Count);
        _output.Last!.Start();
        _output.Last!.Fail();
        Assert.Equal(3, _fetched.Count);
        _output.Last!.Fail();

        Assert.Equal(3, _fetched.Count);
        Assert.Empty(_output.Alive);
    }

    [Fact]
    public void AnOutputThatCannotBeMade_CountsAsAFailure_TriedOnceMore_AndIsLoggedByItsType()
    {
        _output.FailsWith = new System.Runtime.InteropServices.COMException("no media features");

        _player.Select(Set("movie-1", "11"), loop: false);

        Assert.Equal(2, _fetched.Count);
        Assert.Empty(_output.Made);
        Assert.False(_player.IsAudible);
        Assert.Contains("COMException", _logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain("no media features", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public void AFetchThatThrows_CountsAsAFailure_TriedOnceMore()
    {
        Answers.Enqueue(() => Task.FromException<ThemeAudioSource?>(new InvalidOperationException("unforeseen")));
        Answer(Original);

        _player.Select(Set("movie-1", "11"), loop: false);

        Assert.Equal(2, _fetched.Count);
        Playing();
        Assert.Contains("InvalidOperationException", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedGrant_LeavesThePageSilent()
    {
        Answer(null);

        _player.Select(Set("movie-1", "11"), loop: false);

        Assert.Empty(_output.Made);
        Assert.False(_player.IsAudible);
    }

    [Fact]
    public void AnAnswerForAThemeLeftMeanwhile_IsDropped()
    {
        var held = new TaskCompletionSource<ThemeAudioSource?>();
        Answers.Enqueue(() => held.Task);
        _player.Select(Set("movie-1", "11"), loop: false);
        Answer(Original);
        _player.Select(Set("movie-2", "31"), loop: false);

        held.SetResult(Original);

        Assert.Single(_output.Made);
        Assert.Equal([("movie-1", "11"), ("movie-2", "31")], _fetched);
    }

    [Fact]
    public void ASuspendBeforeTheThemeCame_DropsIt_AndTheSameThemeIsFetchedAgain()
    {
        var held = new TaskCompletionSource<ThemeAudioSource?>();
        Answers.Enqueue(() => held.Task);
        _player.Select(Set("movie-1", "11"), loop: false);

        _player.Suspend();
        held.SetResult(Original);
        Answer(Original);
        _player.Select(Set("movie-1", "11"), loop: false);

        Assert.Equal(2, _fetched.Count);
        Assert.Single(_output.Alive);
    }

    [Fact]
    public void ASetWithoutThemes_StopsTheOneThatPlays()
    {
        _player.Select(Set("movie-1", "11"), loop: false);
        var audio = Playing();

        _player.Select(Set("movie-2"), loop: false);
        _clock.Advance(Fade);

        Assert.True(audio.IsDisposed);
        Assert.Single(_fetched);
        Assert.False(_player.IsAudible);
    }
}
