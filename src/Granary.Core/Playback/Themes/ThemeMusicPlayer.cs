using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback.Themes;

/// <summary>
/// Plays the theme of the title page on screen, as the web's <c>lib/themeMusic.ts</c> and the apps' players built on it
/// (Android's <c>ThemeMusicPlayer</c>, Apple's <c>ThemeMusicController</c>) do: the set's first theme, faded in to 35 % over
/// 300 ms and out again, kept playing or resumed in place when the next selection names the same theme, looped in place
/// when it is the original, and played again from a new grant when it is a conversion. A failure is tried again once with a
/// new grant, and audio that plays earns that try back. Themes are optional: a refused grant leaves the page silent.
/// Used on the UI thread only.
/// </summary>
internal sealed class ThemeMusicPlayer
{
    /// <summary>Theme music sits under the page, as on the web.</summary>
    public const double Volume = 0.35;

    private const int FadeSteps = 12;
    public static readonly TimeSpan FadeStep = TimeSpan.FromMilliseconds(25);

    private readonly Func<string, string, CancellationToken, Task<ThemeAudioSource?>> _fetch;
    private readonly IThemeAudioOutput _output;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private IThemeAudio? _audio;
    private Ramp? _fade;
    private CancellationTokenSource? _load;
    // Bumped by every load and stop: an answer or event of an older one is ignored.
    private int _generation;
    private string _owner = "";
    private string _theme = "";
    private bool _loop;
    private bool _converted;
    private bool _played;
    private bool _playing;
    private int _retry;
    private bool _suspended;
    // The theme played to its end and nothing plays it again: the page keeps it chosen, so it does not start over.
    private bool _finished;
    // Themes still fading out after a stop; a stop at once silences them too.
    private readonly List<(IThemeAudio Audio, Ramp Fade)> _retiring = [];

    /// <param name="fetch">A theme's audio by owner and theme id; null when the grant was refused or the fetch failed.</param>
    public ThemeMusicPlayer(Func<string, string, CancellationToken, Task<ThemeAudioSource?>> fetch, IThemeAudioOutput output,
        IUiDispatcher dispatcher, TimeProvider time, ILogger logger)
    {
        _fetch = fetch;
        _output = output;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
    }

    /// <summary>Theme audio may be coming out now, a fade-out included.</summary>
    public bool IsAudible => _playing || _retiring.Count > 0;

    /// <summary>Plays the first theme of <paramref name="selection"/>, or stops when it has none.</summary>
    public void Select(ThemeSongSet? selection, bool loop)
    {
        _loop = loop;
        _audio?.IsLooping = loop && !_converted;
        if (selection?.Items.FirstOrDefault() is not { Id.Length: > 0 } song || selection.OwnerId.Length == 0)
        {
            Stop();
            return;
        }
        if (_owner == selection.OwnerId && _theme == song.Id)
        {
            if (_suspended)
            {
                _suspended = false;
                if (_audio is { } audio)
                {
                    Resume(audio);
                }
                else if (!_finished)
                {
                    StartLoad();
                }
            }
            return;
        }
        Stop();
        _owner = selection.OwnerId;
        _theme = song.Id;
        _retry = 0;
        StartLoad();
    }

    /// <summary>Fades out and pauses, keeping the theme where it is for a selection of the same one.</summary>
    public void Suspend()
    {
        _suspended = true;
        if (_audio is not { } audio)
        {
            if (!_finished)
            {
                Stop(immediate: true);
            }
            return;
        }
        Fade(audio, 0, () =>
        {
            if (_audio == audio && _suspended)
            {
                audio.Pause();
                _playing = false;
            }
        });
    }

    /// <summary>Ends the theme: faded out, or at once when <paramref name="immediate"/> (other media starts, the profile changed).</summary>
    public void Stop(bool immediate = false)
    {
        _suspended = false;
        _finished = false;
        _generation++;
        _load?.Cancel();
        _load?.Dispose();
        _load = null;
        _owner = "";
        _theme = "";
        if (immediate)
        {
            foreach (var (audio, fade) in _retiring)
            {
                fade.Dispose();
                audio.Dispose();
            }
            _retiring.Clear();
        }
        if (_audio is not { } current)
        {
            return;
        }
        _audio = null;
        _fade?.Dispose();
        _fade = null;
        if (!immediate && _playing)
        {
            // Faded out on a ramp of its own, so the next theme's audio neither stops this fade nor is what it lets go of.
            Ramp? retiring = null;
            retiring = new Ramp(current, 0, () =>
            {
                _retiring.RemoveAll(entry => entry.Fade == retiring);
                current.Dispose();
            }, _time, _dispatcher);
            _retiring.Add((current, retiring));
        }
        else
        {
            current.Dispose();
        }
        _playing = false;
    }

    private void StartLoad()
    {
        int generation = ++_generation;
        _load?.Cancel();
        _load?.Dispose();
        var load = _load = new CancellationTokenSource();
        _ = LoadAsync(_owner, _theme, generation, load.Token);
    }

    private async Task LoadAsync(string owner, string theme, int generation, CancellationToken cancellationToken)
    {
        ThemeAudioSource? source;
        try
        {
            source = await _fetch(owner, theme, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // Nothing escapes a load nobody waits for: an unforeseen failure counts as the theme failing.
            Unforeseen(ex, generation);
            return;
        }
        if (generation != _generation)
        {
            return;
        }
        if (source is null)
        {
            // A refused grant or a failed download: tried once more, as audio that breaks off.
            Failed();
            return;
        }
        ClearAudio();
        _converted = source.Converted;
        _played = false;
        _finished = false;
        IThemeAudio audio;
        try
        {
            audio = _output.Create(source.Audio, source.ContentType);
        }
        catch (Exception ex)
        {
            // Windows without its media features (an N edition) has no player to make.
            Unforeseen(ex, generation);
            return;
        }
        audio.Volume = 0;
        audio.IsLooping = _loop && !_converted;
        audio.Started += (_, _) => _dispatcher.Post(() => OnStarted(audio, generation));
        audio.Ended += (_, _) => _dispatcher.Post(() => OnEnded(audio, generation));
        audio.Failed += (_, _) => _dispatcher.Post(() => OnFailed(audio, generation));
        _audio = audio;
        Resume(audio);
    }

    // The fade-in waits for audible audio, as the apps': one run before it would end before anything is heard.
    private void OnStarted(IThemeAudio audio, int generation)
    {
        if (generation != _generation || audio != _audio)
        {
            return;
        }
        bool first = !_played;
        _played = true;
        _retry = 0;
        if (first && !_suspended)
        {
            Fade(audio, Volume);
        }
    }

    private void OnEnded(IThemeAudio audio, int generation)
    {
        if (generation != _generation || audio != _audio)
        {
            return;
        }
        _playing = false;
        // A conversion cannot go back to its start and its grant may have expired, so a loop plays it from a new grant. One
        // that ended without playing spends the same single try as a failure, so a conversion that fails cleanly cannot
        // start over for ever.
        if (_converted && _loop && !_suspended && (_played || SpendRetry()))
        {
            StartLoad();
            return;
        }
        _finished = true;
        ClearAudio();
    }

    private void OnFailed(IThemeAudio audio, int generation)
    {
        if (generation != _generation || audio != _audio)
        {
            return;
        }
        Failed();
    }

    // Logged by its type alone: a message could hold the theme's address.
    private void Unforeseen(Exception ex, int generation)
    {
        _logger.LogWarning("The theme music could not be played: {Failure}", ex.GetType().Name);
        if (generation == _generation)
        {
            Failed();
        }
    }

    // As the web's: the theme is let go, and tried once more with a new grant.
    private void Failed()
    {
        ClearAudio();
        if (!_suspended && SpendRetry())
        {
            StartLoad();
        }
    }

    private void Resume(IThemeAudio audio)
    {
        if (_suspended)
        {
            return;
        }
        audio.Play();
        _playing = true;
        // Before it is first heard, Started fades it in.
        if (_played)
        {
            Fade(audio, Volume);
        }
    }

    private bool SpendRetry() => _retry++ == 0;

    private void Fade(IThemeAudio audio, double to, Action? done = null)
    {
        _fade?.Dispose();
        _fade = new Ramp(audio, to, done, _time, _dispatcher);
    }

    private void ClearAudio()
    {
        _fade?.Dispose();
        _fade = null;
        _audio?.Dispose();
        _audio = null;
        _playing = false;
    }

    /// <summary>
    /// Moves a theme's volume to a level in 12 steps 25 ms apart, as the web's fade, then calls back. The timer's ticks go
    /// through the UI thread; a ramp disposed meanwhile does nothing more.
    /// </summary>
    private sealed class Ramp : IDisposable
    {
        private readonly ITimer _timer;
        private bool _stopped;

        public Ramp(IThemeAudio audio, double to, Action? done, TimeProvider time, IUiDispatcher dispatcher)
        {
            double from = audio.Volume;
            int step = 0;
            _timer = time.CreateTimer(_ => dispatcher.Post(Tick), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(FadeStep, FadeStep);

            void Tick()
            {
                if (_stopped)
                {
                    return;
                }
                step++;
                audio.Volume = Math.Clamp(from + ((to - from) * step / FadeSteps), 0, 1);
                if (step == FadeSteps)
                {
                    Dispose();
                    done?.Invoke();
                }
            }
        }

        public void Dispose()
        {
            _stopped = true;
            _timer.Dispose();
        }
    }
}
