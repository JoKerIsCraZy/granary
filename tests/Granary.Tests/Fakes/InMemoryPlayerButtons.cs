using Granary.Core.Playback;

namespace Granary.Tests.Fakes;

/// <summary>A non-persistent <see cref="IPlayerButtonsLayout"/>: each change says so on the thread that made it, as the real one.</summary>
internal sealed class InMemoryPlayerButtons : IPlayerButtonsLayout
{
    private volatile PlayerBar _current = PlayerBar.Default;

    public PlayerBar Current => _current;

    public event EventHandler<PlayerBar>? Changed;

    /// <summary>Something follows the bar, such as an open player.</summary>
    public bool IsFollowed => Changed is not null;

    public void Set(PlayerBar bar)
    {
        if (bar.Equals(_current))
        {
            return;
        }
        _current = bar;
        Changed?.Invoke(this, bar);
    }

    public void SetShown(PlayerButton button, bool shown) => Set(_current.WithShown(button, shown));

    /// <summary>Changes <see cref="Current"/> without saying so, as another profile's bar would read before the player hears of it.</summary>
    public void SetUnannounced(PlayerBar bar) => _current = bar;
}
