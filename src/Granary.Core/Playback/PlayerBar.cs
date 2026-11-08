namespace Granary.Core.Playback;

/// <summary>
/// A button on the right of the player's bar, which Settings → Playback → Player buttons can hide and move. The transport
/// in the middle, the seek bar, Exit and Minimize, and the minimized player's bar stay as they are.
/// </summary>
public enum PlayerButton
{
    /// <summary>Mute and the volume slider.</summary>
    Volume,
    SleepTimer,
    Audio,
    Subtitles,
    Chapters,

    /// <summary>The gear: speed, audio delay, version and quality.</summary>
    Settings,
    EditMarkers,
    PlaybackInfo,
    MiniView,
    Fill,
    FullScreen,
}

/// <summary>A player button as Settings lists it: its name in <c>player-buttons.json</c>, its label, and what to know about it.</summary>
public sealed record PlayerButtonInfo(PlayerButton Button, string Id, string Label, string? Note);

/// <summary>
/// The right of the player's bar as a profile has it: every button in its order, and the ones it hides. Granary's own:
/// Silo's clients have no such editor. A button that needs something, such as chapters, still shows only with it.
/// </summary>
public sealed class PlayerBar : IEquatable<PlayerBar>
{
    /// <summary>Every button, in the bar's own order.</summary>
    public static IReadOnlyList<PlayerButtonInfo> Buttons { get; } =
    [
        new(PlayerButton.Volume, "volume", "Volume", "Mute and the volume slider."),
        new(PlayerButton.SleepTimer, "sleep_timer", "Sleep timer", "Hiding it ends a timer that's running."),
        new(PlayerButton.Audio, "audio", "Audio tracks", "Shows when a title has audio tracks."),
        new(PlayerButton.Subtitles, "subtitles", "Subtitles", "Shows for titles from your Silo server."),
        new(PlayerButton.Chapters, "chapters", "Chapters", "Shows when a title has chapters."),
        new(PlayerButton.Settings, "settings", "Playback settings", "Speed, audio delay, version and quality."),
        new(PlayerButton.EditMarkers, "edit_markers", "Edit markers", "Shows when your account may edit markers."),
        new(PlayerButton.PlaybackInfo, "playback_info", "Playback info", null),
        new(PlayerButton.MiniView, "mini_view", "Mini view", null),
        new(PlayerButton.Fill, "fill", "Fill screen", null),
        new(PlayerButton.FullScreen, "full_screen", "Full screen", null),
    ];

    private static readonly PlayerButton[] DefaultOrder = [.. Buttons.Select(info => info.Button)];

    // The hidden buttons in the bar's own order, so two bars that hide the same are equal and save the same.
    private readonly PlayerButton[] _hidden;

    /// <summary>
    /// A bar with <paramref name="order"/>, the buttons the profile placed, and <paramref name="hidden"/>. A button it has
    /// not placed, one an update added, takes its own place: after the one before it in the bar's own order.
    /// </summary>
    public PlayerBar(IEnumerable<PlayerButton> order, IEnumerable<PlayerButton> hidden)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(hidden);
        Order = Arrange(order);
        var hides = hidden.ToHashSet();
        _hidden = [.. DefaultOrder.Where(hides.Contains)];
    }

    /// <summary>Every button shown, in the bar's own order.</summary>
    public static PlayerBar Default { get; } = new([], []);

    /// <summary>Every button, in the profile's order.</summary>
    public IReadOnlyList<PlayerButton> Order { get; }

    /// <summary>The buttons hidden, in the bar's own order.</summary>
    public IReadOnlyList<PlayerButton> Hidden => _hidden;

    /// <summary>Every button shows, in the bar's own order.</summary>
    public bool IsDefault => _hidden.Length == 0 && HasDefaultOrder;

    /// <summary>The buttons are in the bar's own order, shown or not.</summary>
    public bool HasDefaultOrder => Order.SequenceEqual(DefaultOrder);

    public bool Volume => Shows(PlayerButton.Volume);

    public bool SleepTimer => Shows(PlayerButton.SleepTimer);

    public bool Audio => Shows(PlayerButton.Audio);

    public bool Subtitles => Shows(PlayerButton.Subtitles);

    public bool Chapters => Shows(PlayerButton.Chapters);

    public bool Settings => Shows(PlayerButton.Settings);

    public bool EditMarkers => Shows(PlayerButton.EditMarkers);

    public bool PlaybackInfo => Shows(PlayerButton.PlaybackInfo);

    public bool MiniView => Shows(PlayerButton.MiniView);

    public bool Fill => Shows(PlayerButton.Fill);

    public bool FullScreen => Shows(PlayerButton.FullScreen);

    public static PlayerButtonInfo InfoOf(PlayerButton button) => Buttons[Array.IndexOf(DefaultOrder, button)];

    public bool Shows(PlayerButton button) => Array.IndexOf(_hidden, button) < 0;

    public PlayerBar WithShown(PlayerButton button, bool shown) =>
        new(Order, shown ? _hidden.Where(hidden => hidden != button) : [.. _hidden, button]);

    /// <summary>The bar with <paramref name="button"/> one place earlier (<paramref name="delta"/> -1) or later (+1); the first and last stay.</summary>
    public PlayerBar WithMoved(PlayerButton button, int delta)
    {
        var order = Order.ToList();
        int from = order.IndexOf(button);
        int to = from + Math.Sign(delta);
        if (from < 0 || to < 0 || to >= order.Count || from == to)
        {
            return this;
        }
        (order[from], order[to]) = (order[to], order[from]);
        return new(order, _hidden);
    }

    public bool Equals(PlayerBar? other) =>
        other is not null && Order.SequenceEqual(other.Order) && _hidden.SequenceEqual(other._hidden);

    public override bool Equals(object? obj) => Equals(obj as PlayerBar);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var button in Order)
        {
            hash.Add(button);
        }
        hash.Add(-1);
        foreach (var button in _hidden)
        {
            hash.Add(button);
        }
        return hash.ToHashCode();
    }

    // The profile's buttons first, each once and only those that still exist; then each one it has not placed, after the
    // one before it in the bar's own order, or first when none is.
    private static PlayerButton[] Arrange(IEnumerable<PlayerButton> order)
    {
        var arranged = order.Where(button => Array.IndexOf(DefaultOrder, button) >= 0).Distinct().ToList();
        for (int index = 0; index < DefaultOrder.Length; index++)
        {
            var button = DefaultOrder[index];
            if (arranged.Contains(button))
            {
                continue;
            }
            int at = 0;
            for (int before = index - 1; before >= 0; before--)
            {
                int placed = arranged.IndexOf(DefaultOrder[before]);
                if (placed >= 0)
                {
                    at = placed + 1;
                    break;
                }
            }
            arranged.Insert(at, button);
        }
        return [.. arranged];
    }
}
