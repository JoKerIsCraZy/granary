using Granary.Core.Remote;

namespace Granary.Tests.Fakes;

/// <summary>An advertiser that registers nothing with Windows: it notes each registration and its removal.</summary>
internal sealed class FakeServiceAdvertiser(RemoteCalls calls) : IServiceAdvertiser
{
    private readonly List<ServiceRegistration> _registrations = [];

    /// <summary>Whether Windows takes a registration.</summary>
    public bool Accepts { get; set; } = true;

    /// <summary>What a registration throws, if anything.</summary>
    public Exception? Throws { get; set; }

    /// <summary>What a removal waits for: nothing, unless a test holds it up.</summary>
    public Task UnregisterWaitsFor { get; set; } = Task.CompletedTask;

    public bool UnregisterStarted { get; private set; }

    /// <summary>What is registered now.</summary>
    public ServiceRegistration? Current { get; private set; }

    public IReadOnlyList<ServiceRegistration> Registrations
    {
        get
        {
            lock (_registrations)
            {
                return [.. _registrations];
            }
        }
    }

    public Task<bool> RegisterAsync(ServiceRegistration registration, CancellationToken cancellationToken)
    {
        string playing = registration.Txt.First(pair => pair.Key == "playing").Value;
        calls.Add($"advertiser.Register({registration.Port}, playing={playing})");
        if (Throws is { } failure)
        {
            return Task.FromException<bool>(failure);
        }
        lock (_registrations)
        {
            _registrations.Add(registration);
        }
        Current = Accepts ? registration : null;
        return Task.FromResult(Accepts);
    }

    public async Task UnregisterAsync()
    {
        calls.Add("advertiser.Unregister");
        UnregisterStarted = true;
        await UnregisterWaitsFor;
        Current = null;
    }

    /// <summary>A TXT value of the current registration.</summary>
    public string? Txt(string key) => Current?.Txt.FirstOrDefault(pair => pair.Key == key).Value;
}
