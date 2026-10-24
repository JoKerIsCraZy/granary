using Granary.Core.Abstractions;

namespace Granary.Tests.Fakes;

/// <summary>Records navigation requests of the server management view models.</summary>
internal sealed class ServersRecordingNavigation : INavigationService
{
    public List<(AppRoute Route, object? Parameter)> Navigations { get; } = [];

    public AppRoute? Current { get; private set; }

    public void NavigateTo(AppRoute route, object? parameter = null)
    {
        Navigations.Add((route, parameter));
        Current = route;
    }
}
