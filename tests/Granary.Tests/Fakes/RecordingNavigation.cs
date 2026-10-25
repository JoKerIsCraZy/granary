using Granary.Core.Abstractions;

namespace Granary.Tests.Fakes;

/// <summary>Records navigation requests; <see cref="ResetTo"/> also clears the recorded history.</summary>
internal sealed class RecordingNavigation : INavigationService
{
    private readonly Stack<(AppRoute Route, object? Parameter)> _history = new();

    public List<(AppRoute Route, object? Parameter)> Navigations { get; } = [];

    /// <summary>Routes that were reached through <see cref="ResetTo"/>.</summary>
    public List<AppRoute> Resets { get; } = [];

    public AppRoute? Current { get; private set; }

    public bool CanGoBack => _history.Count > 0;

    public int BackCount { get; private set; }

    public void NavigateTo(AppRoute route, object? parameter = null)
    {
        if (Current is { } current)
        {
            _history.Push((current, null));
        }
        Navigations.Add((route, parameter));
        Current = route;
    }

    public void ResetTo(AppRoute route, object? parameter = null)
    {
        _history.Clear();
        Resets.Add(route);
        Navigations.Add((route, parameter));
        Current = route;
    }

    public void GoBack()
    {
        BackCount++;
        if (_history.TryPop(out var previous))
        {
            Current = previous.Route;
        }
    }
}
