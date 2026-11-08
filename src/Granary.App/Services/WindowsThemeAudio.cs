using Granary.Core.Api.Catalog;
using Granary.Core.Playback.Themes;
using Microsoft.Extensions.Logging;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Granary.App.Services;

/// <summary>
/// Title pages' theme music through Windows' own media player (<c>Windows.Media.Playback.MediaPlayer</c>), a light player beside
/// mpv: each theme plays from the bytes Core downloaded, so no address or token reaches it. It stays out of Windows' media
/// overlay and media keys, which belong to Granary's player.
/// </summary>
internal sealed class WindowsThemeAudio(ILogger<WindowsThemeAudio> logger) : IThemeAudioOutput
{
    /// <summary>
    /// What Media Foundation plays on every Windows 10 and 11 with its media features: MP3, AAC in MP4 and ADTS, ALAC, FLAC and
    /// PCM WAV. Ogg Vorbis and Opus need an extension from the Store, so the server converts those to AAC where it can.
    /// </summary>
    public IReadOnlyList<ThemeAudioFormat> Formats { get; } =
    [
        new("mp3", "mp3"),
        new("mp4", "aac"),
        new("m4a", "aac"),
        new("m4a", "alac"),
        new("aac", "aac"),
        new("flac", "flac"),
        new("wav", "pcm"),
    ];

    public IThemeAudio Create(ReadOnlyMemory<byte> audio, string? contentType) => new Theme(audio, contentType, logger);

    private sealed class Theme : IThemeAudio
    {
        private readonly MediaPlayer _player;
        private readonly MediaSource _source;
        private readonly ILogger _logger;
        private volatile bool _disposed;
        private int _started;

        public Theme(ReadOnlyMemory<byte> audio, string? contentType, ILogger logger)
        {
            _logger = logger;
            // The stream is the player's from here on: MediaSource keeps reading it, and closing the source closes it.
            var stream = new MemoryStream(audio.ToArray(), writable: false).AsRandomAccessStream();
            _source = MediaSource.CreateFromStream(stream, contentType ?? "");
            _player = new MediaPlayer
            {
                AutoPlay = false,
                Volume = 0,
                AudioCategory = MediaPlayerAudioCategory.Media,
            };
            // Not in Windows' media overlay, and the media keys stay with Granary's player.
            _player.CommandManager.IsEnabled = false;
            _player.MediaEnded += OnEnded;
            _player.MediaFailed += OnFailed;
            _player.PlaybackSession.PositionChanged += OnPositionChanged;
            _player.Source = _source;
        }

        public event EventHandler? Started;

        public event EventHandler? Ended;

        public event EventHandler? Failed;

        public double Volume
        {
            get => _disposed ? 0 : _player.Volume;
            set
            {
                if (!_disposed)
                {
                    _player.Volume = value;
                }
            }
        }

        public bool IsLooping
        {
            get => !_disposed && _player.IsLoopingEnabled;
            set
            {
                if (!_disposed)
                {
                    _player.IsLoopingEnabled = value;
                }
            }
        }

        public void Play()
        {
            if (!_disposed)
            {
                _player.Play();
            }
        }

        public void Pause()
        {
            if (!_disposed)
            {
                _player.Pause();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _player.MediaEnded -= OnEnded;
            _player.MediaFailed -= OnFailed;
            _player.PlaybackSession.PositionChanged -= OnPositionChanged;
            _player.Pause();
            _player.Source = null;
            _player.Dispose();
            _source.Dispose();
        }

        // Windows raises these on threads of its own; Core takes them to the UI thread.
        private void OnPositionChanged(MediaPlaybackSession session, object args)
        {
            if (!_disposed && session.Position > TimeSpan.Zero && Interlocked.Exchange(ref _started, 1) == 0)
            {
                Started?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OnEnded(MediaPlayer sender, object args)
        {
            if (!_disposed)
            {
                Ended?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            if (_disposed)
            {
                return;
            }
            // The kind of failure only: nothing of the theme's address ever reaches the player.
            _logger.LogInformation("Windows could not play the theme music: {Error}", args.Error);
            Failed?.Invoke(this, EventArgs.Empty);
        }
    }
}
