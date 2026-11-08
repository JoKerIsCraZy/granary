using Granary.Core.Api.Catalog;

namespace Granary.Core.Playback.Themes;

/// <summary>No sound: what Core uses until the app brings its own output.</summary>
internal sealed class NoThemeAudioOutput : IThemeAudioOutput
{
    public IReadOnlyList<ThemeAudioFormat> Formats { get; } = [];

    public IThemeAudio Create(ReadOnlyMemory<byte> audio, string? contentType) => new Silence();

    private sealed class Silence : IThemeAudio
    {
        public double Volume { get; set; }

        public bool IsLooping { get; set; }

        public void Play()
        {
        }

        public void Pause()
        {
        }

        public void Dispose()
        {
        }

        // A silent theme raises nothing.
        public event EventHandler? Started
        {
            add { }
            remove { }
        }

        public event EventHandler? Ended
        {
            add { }
            remove { }
        }

        public event EventHandler? Failed
        {
            add { }
            remove { }
        }
    }
}
