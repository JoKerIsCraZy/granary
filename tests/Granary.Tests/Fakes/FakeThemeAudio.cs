using Granary.Core.Api.Catalog;
using Granary.Core.Playback.Themes;

namespace Granary.Tests.Fakes;

/// <summary>An audio output that only remembers what it was told: each theme made, its volume, whether it plays.</summary>
internal sealed class FakeThemeAudioOutput : IThemeAudioOutput
{
    public IReadOnlyList<ThemeAudioFormat> Formats { get; set; } = [new("mp3", "mp3"), new("mp4", "aac")];

    /// <summary>Every theme made, oldest first.</summary>
    public List<FakeThemeAudio> Made { get; } = [];

    public FakeThemeAudio? Last => Made.LastOrDefault();

    /// <summary>The themes not let go of yet.</summary>
    public IEnumerable<FakeThemeAudio> Alive => Made.Where(audio => !audio.IsDisposed);

    public IThemeAudio Create(ReadOnlyMemory<byte> audio, string? contentType)
    {
        var made = new FakeThemeAudio(audio.ToArray(), contentType);
        Made.Add(made);
        return made;
    }
}

internal sealed class FakeThemeAudio(byte[] audio, string? contentType) : IThemeAudio
{
    public byte[] Audio { get; } = audio;

    public string? ContentType { get; } = contentType;

    public double Volume { get; set; }

    public bool IsLooping { get; set; }

    public bool IsPlaying { get; private set; }

    public int Plays { get; private set; }

    public bool IsDisposed { get; private set; }

    public event EventHandler? Started;

    public event EventHandler? Ended;

    public event EventHandler? Failed;

    public void Play()
    {
        Plays++;
        IsPlaying = true;
    }

    public void Pause() => IsPlaying = false;

    public void Dispose()
    {
        IsDisposed = true;
        IsPlaying = false;
    }

    /// <summary>Playback moved past the start.</summary>
    public void Start() => Started?.Invoke(this, EventArgs.Empty);

    public void End()
    {
        IsPlaying = false;
        Ended?.Invoke(this, EventArgs.Empty);
    }

    public void Fail()
    {
        IsPlaying = false;
        Failed?.Invoke(this, EventArgs.Empty);
    }
}
