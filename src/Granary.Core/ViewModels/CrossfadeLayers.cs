namespace Granary.Core.ViewModels;

/// <summary>
/// Two picture layers that take turns, so a new picture fades in over the shown one, which stays fully shown underneath
/// until the fade is done; a third picture asked for during a fade waits for it, and only the last one asked for follows.
/// </summary>
/// <typeparam name="T">What a layer shows.</typeparam>
public sealed class CrossfadeLayers<T>
    where T : class
{
    private int _previous = -1;
    private T? _waiting;

    /// <summary>The layer on top (0 or 1); -1 before any.</summary>
    public int Front { get; private set; } = -1;

    /// <summary>A fade is running.</summary>
    public bool IsFading { get; private set; }

    /// <summary>
    /// The layer to fill with <paramref name="picture"/> and fade in on top; null when a fade is running, the picture then
    /// waiting for <see cref="Complete"/>.
    /// </summary>
    public int? Begin(T picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        if (IsFading)
        {
            _waiting = picture;
            return null;
        }
        IsFading = true;
        _previous = Front;
        Front = Front < 0 ? 0 : 1 - Front;
        return Front;
    }

    /// <summary>The fade is done: the layer to hide now (null for none), and the picture that waited, to begin next.</summary>
    public (int? Hide, T? Next) Complete()
    {
        IsFading = false;
        int? hide = _previous >= 0 ? _previous : null;
        _previous = -1;
        var next = _waiting;
        _waiting = null;
        return (hide, next);
    }

    /// <summary>Both layers were emptied.</summary>
    public void Reset()
    {
        Front = -1;
        _previous = -1;
        IsFading = false;
        _waiting = null;
    }
}
