using Granary.Core.Abstractions;

namespace Granary.Tests.Fakes;

/// <summary>Records navigation requests.</summary>
internal sealed class RecordingNavigation : INavigationService
{
    public List<(AppRoute Route, object? Parameter)> Navigations { get; } = [];

    public AppRoute? Current { get; private set; }

    public void NavigateTo(AppRoute route, object? parameter = null)
    {
        Navigations.Add((route, parameter));
        Current = route;
    }
}
