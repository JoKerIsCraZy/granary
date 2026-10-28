using Microsoft.Extensions.Logging;

namespace Granary.Core.Servers;

/// <summary>A Silo server found on this network and confirmed by its identity.</summary>
/// <param name="Url"><c>http://address:port/</c> from the resolved address, never the advertised <c>.local</c> name.</param>
public sealed record DiscoveredServer(string ServerId, string Name, Uri Url)
{
    /// <summary>"On this network · 192.168.1.6:8090": the port stays, so two deployments on one host differ.</summary>
    public string Detail => $"On this network · {Url.Authority}";
}

/// <summary>
/// Lists the Silo servers advertised on this network while the add-server screen shows, as Android's
/// <c>LanServerDiscovery</c> and Apple's <c>ServerDiscovery</c> do (silo-server <c>docs/architecture/server-discovery.md</c>).
/// An advert is a hint: its TXT <c>id</c> names a deployment, but anyone on the network can advertise anything. A
/// server is listed only when <c>GET /api/v2/system/identity</c> at its resolved address answers that same id, and
/// picking it is the same as typing its address. Adverts with one id (replicas, a host seen on several interfaces)
/// share one row.
/// </summary>
public sealed class LanServerDiscovery
{
    /// <summary>Discovery runs while someone looks at the screen: a dead address must give up quickly.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan BrowseRetryDelay = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    internal const int MaxAttempts = 2;
    internal const int MaxMisses = 3;
    private const int MaxConcurrentProbes = 4;

    private readonly ILanServiceBrowser _browser;
    private readonly ILanServerProbe _probe;
    private readonly TimeProvider _time;
    private readonly ILogger<LanServerDiscovery> _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _probeSlots = new(MaxConcurrentProbes);

    // Keyed by the browser's result key; an entry is replaced, never edited, when its advert changes.
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private IReadOnlyList<DiscoveredServer> _servers = [];
    private CancellationTokenSource? _run;
    private ITimer? _refresh;
    private ITimer? _browseRetry;

    // Advances on start and stop, and with every browse: events an earlier one queued are dropped.
    private int _session;
    private int _browseGeneration;
    private bool _browseFailed;
    private bool _quickRetryUsed;

    public LanServerDiscovery(ILanServiceBrowser browser, ILanServerProbe probe, TimeProvider time, ILogger<LanServerDiscovery> logger)
    {
        _browser = browser;
        _probe = probe;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised on any thread when <see cref="Servers"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The confirmed servers, one per id, by name.</summary>
    public IReadOnlyList<DiscoveredServer> Servers
    {
        get
        {
            lock (_gate)
            {
                return _servers;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_run is not null)
            {
                return;
            }
            _run = new CancellationTokenSource();
            int session = ++_session;
            _quickRetryUsed = false;
            StartBrowse(session);
            _refresh = _time.CreateTimer(_ => Refresh(session), null, RefreshInterval, RefreshInterval);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_run is not { } run)
            {
                return;
            }
            _run = null;
            _session++;
            _refresh?.Dispose();
            _browseRetry?.Dispose();
            _browser.Stop();
            run.Cancel();
            run.Dispose();
            ClearEntries();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void StartBrowse(int session)
    {
        _browseFailed = false;
        _browser.Start(new SessionEvents(this, session, ++_browseGeneration));
    }

    // Every 30 s: restart a browse that failed, and check the listed rows again (a server keeps its advert when its
    // address moves, and a renamed one gets its new name), giving adverts that failed both checks another pair.
    private void Refresh(int session)
    {
        lock (_gate)
        {
            if (_session != session || _run is null)
            {
                return;
            }
            if (_browseFailed)
            {
                RestartBrowse(session);
            }
            else
            {
                foreach (var (key, entry) in _entries.Where(e => e.Value.Job is null))
                {
                    Confirm(key, entry);
                }
                return;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RestartBrowse(int session)
    {
        _browser.Stop();
        ClearEntries();
        StartBrowse(session);
    }

    private void OnResolved(string key, LanAdvertisement advertisement)
    {
        string? serverId = LanAdverts.ServerId(advertisement.TextRecords);
        var origins = LanAdverts.Origins(advertisement.Addresses, advertisement.Port);
        if (serverId is null)
        {
            OnGone(key);
            return;
        }
        // A resolve can carry only an address a URL cannot use: keep what the last one found.
        if (origins.Count == 0)
        {
            return;
        }
        _entries.TryGetValue(key, out var existing);
        if (existing is not null && existing.ServerId == serverId && existing.Origins.SequenceEqual(origins)
            && existing.InstanceName == advertisement.InstanceName)
        {
            return;
        }
        existing?.Cancel();
        var entry = new Entry(serverId, origins, advertisement.InstanceName);
        // A row keeps showing while its address is still advertised; a moved address or another id is checked afresh.
        if (existing is { Row: { } row } && existing.ServerId == serverId && origins.Contains(row.Url))
        {
            entry.Row = row;
            entry.Misses = existing.Misses;
        }
        _entries[key] = entry;
        Publish();
        Confirm(key, entry);
    }

    private void OnGone(string key)
    {
        if (_entries.Remove(key, out var entry))
        {
            entry.Cancel();
            Publish();
        }
    }

    // A browse can fail while the last visit's is still stopping: one quick retry, later ones wait for the refresh.
    private void OnBrowseFailed(int session)
    {
        _browseFailed = true;
        _logger.LogInformation("Browsing the network for Silo servers stopped");
        if (_quickRetryUsed)
        {
            return;
        }
        _quickRetryUsed = true;
        _browseRetry?.Dispose();
        _browseRetry = _time.CreateTimer(_ =>
        {
            lock (_gate)
            {
                if (_session != session || !_browseFailed || _run is null)
                {
                    return;
                }
                RestartBrowse(session);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }, null, BrowseRetryDelay, Timeout.InfiniteTimeSpan);
    }

    // A new advert gets two tries a few seconds apart; a listed row one per refresh, and it stays through two failures
    // in a row, so a probe that times out during a network change does not pull it from under the person picking it.
    private void Confirm(string key, Entry entry)
    {
        entry.Cancel();
        var job = CancellationTokenSource.CreateLinkedTokenSource(_run!.Token);
        entry.Job = job;
        _ = Task.Run(() => ConfirmAsync(key, entry, job));
    }

    private async Task ConfirmAsync(string key, Entry entry, CancellationTokenSource job)
    {
        var token = job.Token;
        try
        {
            int attempts = entry.Row is null ? MaxAttempts : 1;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(RetryDelay, _time, token).ConfigureAwait(false);
                }
                var found = await ConfirmAtAsync(entry, token).ConfigureAwait(false);
                if (found is not null)
                {
                    Settle(key, entry, job, found);
                    return;
                }
            }
            Settle(key, entry, job, found: null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Confirming a server found on the network failed");
        }
    }

    private void Settle(string key, Entry entry, CancellationTokenSource job, DiscoveredServer? found)
    {
        lock (_gate)
        {
            // Replaced or removed meanwhile: the newer entry reports.
            if (!_entries.TryGetValue(key, out var current) || current != entry || entry.Job != job)
            {
                return;
            }
            entry.Job = null;
            job.Dispose();
            if (found is not null)
            {
                entry.Row = found;
                entry.Misses = 0;
            }
            else if (entry.Row is not null && ++entry.Misses >= MaxMisses)
            {
                entry.Row = null;
                entry.Misses = 0;
            }
            Publish();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The first origin whose identity answers the advertised id, named by its branding.</summary>
    private async Task<DiscoveredServer?> ConfirmAtAsync(Entry entry, CancellationToken cancellationToken)
    {
        foreach (var origin in entry.Origins)
        {
            await _probeSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var identityTimeout = ProbeDeadline(cancellationToken);
                string? id = await _probe.GetServerIdAsync(origin, identityTimeout.Token).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (id != entry.ServerId)
                {
                    continue;
                }
                using var nameTimeout = ProbeDeadline(cancellationToken);
                string? name = await _probe.GetNameAsync(origin, nameTimeout.Token).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new DiscoveredServer(entry.ServerId, LanAdverts.DisplayName(name, entry.InstanceName), origin);
            }
            finally
            {
                _probeSlots.Release();
            }
        }
        return null;
    }

    private static CancellationTokenSource ProbeDeadline(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        return timeout;
    }

    private void ClearEntries()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Cancel();
        }
        _entries.Clear();
        Publish();
    }

    // One row per server: of several confirmed adverts with one id, the one with the fewest failed checks, then the
    // lowest address; then by name.
    private void Publish() => _servers =
    [
        .. _entries.Values
            .Where(e => e.Row is not null)
            .OrderBy(e => e.Misses)
            .ThenBy(e => e.Row!.Url.AbsoluteUri, StringComparer.Ordinal)
            .DistinctBy(e => e.ServerId, StringComparer.Ordinal)
            .Select(e => e.Row!)
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.Url.AbsoluteUri, StringComparer.Ordinal),
    ];

    private sealed class Entry(string serverId, IReadOnlyList<Uri> origins, string instanceName)
    {
        public string ServerId { get; } = serverId;

        public IReadOnlyList<Uri> Origins { get; } = origins;

        public string InstanceName { get; } = instanceName;

        /// <summary>The last confirmed row, listed while set.</summary>
        public DiscoveredServer? Row { get; set; }

        /// <summary>Failed checks of <see cref="Row"/> in a row.</summary>
        public int Misses { get; set; }

        /// <summary>The check on its way, if any.</summary>
        public CancellationTokenSource? Job { get; set; }

        public void Cancel()
        {
            Job?.Cancel();
            Job = null;
        }
    }

    // Browser callbacks, dropped once their visit or browse is over; the list's word goes out after the lock.
    private sealed class SessionEvents(LanServerDiscovery owner, int session, int generation) : ILanBrowseEvents
    {
        public void Resolved(string key, LanAdvertisement advertisement) => Post(() => owner.OnResolved(key, advertisement));

        public void Gone(string key) => Post(() => owner.OnGone(key));

        public void Failed() => Post(() => owner.OnBrowseFailed(session));

        private void Post(Action change)
        {
            lock (owner._gate)
            {
                if (owner._session != session || owner._browseGeneration != generation || owner._run is null)
                {
                    return;
                }
                change();
            }
            owner.Changed?.Invoke(owner, EventArgs.Empty);
        }
    }
}
