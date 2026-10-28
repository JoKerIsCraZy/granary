namespace Granary.App.Views;

/// <summary>
/// A page that asks before it is left, as the web's unsaved-changes guard: the navigation waits for its answer and goes
/// on only when it lets go. A new start (sign-in, another profile) does not ask.
/// </summary>
internal interface ILeaveGuard
{
    /// <summary>Leaving now would lose something.</summary>
    bool AsksBeforeLeaving { get; }

    /// <summary>Asks; true lets the navigation go on, and the page does not ask again.</summary>
    Task<bool> ConfirmLeaveAsync();

    /// <summary>The navigation it let go did not leave it after all: it asks again next time.</summary>
    void Rearm();
}
