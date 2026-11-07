namespace Granary.Core.Navigation;

/// <summary>
/// The focus a start places on its first page (MainWindow.Remote.cs): Home's first card, as Silo's TV app starts on its
/// first row, placed quietly, so a mouse user sees no focus ring. The remote's first arrow or OK only shows it where it is,
/// moving and opening nothing, so the viewer sees their place before anything happens. A pointer press, or focus going
/// anywhere else, ends that.
/// </summary>
public sealed class QuietStartFocus
{
    // Weak: the page may go before the viewer ever presses a key.
    private WeakReference<object>? _placed;

    /// <summary>The start gave <paramref name="element"/> focus, not shown.</summary>
    public void Place(object element) => _placed = new(element);

    /// <summary>Focus went to <paramref name="element"/>; anywhere but the placed element ends the quiet focus.</summary>
    public void FocusMoved(object element)
    {
        if (!IsPlaced(element))
        {
            _placed = null;
        }
    }

    /// <summary>A pointer press: the viewer has taken over.</summary>
    public void End() => _placed = null;

    /// <summary>
    /// Whether a key was taken to show focus where it is, with <paramref name="show"/>. The first arrow or OK (Enter or
    /// Space) is, while focus is still on the placed element and not shown; that key ends the quiet focus, as does one on
    /// any other element. A show that fails passes the key on and leaves the quiet focus waiting. Other keys pass and
    /// leave it.
    /// </summary>
    public bool ShowsInPlace(bool isArrowOrOk, object? focused, bool isShown, Func<bool> show)
    {
        if (!isArrowOrOk || _placed is null)
        {
            return false;
        }
        if (focused is null || !IsPlaced(focused) || isShown)
        {
            _placed = null;
            return false;
        }
        if (!show())
        {
            return false;
        }
        _placed = null;
        return true;
    }

    private bool IsPlaced(object element) =>
        _placed is not null && _placed.TryGetTarget(out var placed) && ReferenceEquals(placed, element);
}
