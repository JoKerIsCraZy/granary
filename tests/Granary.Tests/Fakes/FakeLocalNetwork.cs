using System.Net;
using Granary.Core.Servers;

namespace Granary.Tests.Fakes;

/// <summary>A network whose names and changes the test sets.</summary>
internal sealed class FakeLocalNetwork : ILocalNetwork
{
    public Dictionary<string, IPAddress[]> Addresses { get; } = [];

    public event EventHandler? Changed;

    public bool IsFollowed => Changed is not null;

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Task.FromResult(Addresses.TryGetValue(host, out var addresses) ? addresses : []);

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
