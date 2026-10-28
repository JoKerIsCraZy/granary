using Granary.Core.Servers;

namespace Granary.Tests.Fakes;

/// <summary>A DNS-SD browser the test drives: it reports what the test advertises to the browse running then.</summary>
internal sealed class FakeLanBrowser : ILanServiceBrowser
{
    private readonly Lock _gate = new();

    public int Starts { get; private set; }

    public int Stops { get; private set; }

    public bool IsBrowsing => Current is not null;

    /// <summary>The events of the browse running now, or null.</summary>
    public ILanBrowseEvents? Current { get; private set; }

    /// <summary>Events of earlier browses, still callable to see that they are dropped.</summary>
    public List<ILanBrowseEvents> Earlier { get; } = [];

    public void Start(ILanBrowseEvents events)
    {
        lock (_gate)
        {
            Starts++;
            if (Current is not null)
            {
                Earlier.Add(Current);
            }
            Current = events;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            Stops++;
            if (Current is not null)
            {
                Earlier.Add(Current);
            }
            Current = null;
        }
    }

    public void Advertise(string key, string? serverId, int port = 8090, string instance = "Silo", params string[] addresses) =>
        Current!.Resolved(key, Advert(serverId, port, instance, addresses));

    public void Lose(string key) => Current!.Gone(key);

    public void Fail() => Current!.Failed();

    public static LanAdvertisement Advert(string? serverId, int port = 8090, string instance = "Silo", params string[] addresses) =>
        new(instance, addresses.Length > 0 ? addresses : ["192.168.1.6"], port, serverId is null ? ["v=1"] : ["v=1", $"id={serverId}"]);
}

/// <summary>Answers identity and branding per origin as the test scripts them; anything else answers nothing.</summary>
internal sealed class FakeLanProbe : ILanServerProbe
{
    private readonly Lock _gate = new();

    public Dictionary<string, string?> Ids { get; } = [];

    public Dictionary<string, string?> Names { get; } = [];

    public List<string> Asked { get; } = [];

    public int IdentityCalls(string origin)
    {
        lock (_gate)
        {
            return Asked.Count(a => a == origin);
        }
    }

    public Task<string?> GetServerIdAsync(Uri origin, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Asked.Add(origin.AbsoluteUri);
            return Task.FromResult(Ids.GetValueOrDefault(origin.AbsoluteUri));
        }
    }

    public Task<string?> GetNameAsync(Uri origin, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(Names.GetValueOrDefault(origin.AbsoluteUri));
        }
    }
}
