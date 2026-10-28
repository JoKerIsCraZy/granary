using Microsoft.Extensions.Logging;

namespace Granary.Core.Servers;

/// <summary>
/// The <c>_silo._tcp</c> adverts of the active server, for its local route (server-identity.md, rule 1): adverts whose
/// TXT <c>id</c> is the server's identity, each reduced to HTTPS on a private IPv4 address and its port. They are only
/// candidates: <see cref="LocalRouteMonitor"/> uses one only when it presents the pinned key and answers as the server.
/// Browses while <see cref="Watch"/> names a server, as Apple's <c>LocalRouteController</c> does. Only that server's
/// adverts are kept, at most eight, in the order they came: anyone can advertise its id, so later adverts cannot
/// push out one that came first, and the origin last confirmed goes first (<see cref="Origins"/>).
/// </summary>
public sealed class LanAdvertWatch(ILanServiceBrowser browser, ILogger<LanAdvertWatch> logger)
{
    /// <summary>The watched server's adverts kept at once; later ones are dropped until one goes.</summary>
    internal const int MaxAdverts = 8;

    private readonly Lock _gate = new();
    private readonly ILogger<LanAdvertWatch> _logger = logger;

    // The watched server's adverts by browse key, each with the order it came in.
    private readonly Dictionary<string, (IReadOnlyList<Uri> Origins, long Order)> _adverts = new(StringComparer.Ordinal);
    private string? _serverId;
    private bool _browsing;
    private bool _failed;
    private int _generation;
    private long _order;

    /// <summary>Raised on any thread when the watched server's advertised origins changed.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Browses for <paramref name="serverId"/>'s adverts. Another server, or a browse that stopped on its own, starts a
    /// new browse, which reports again what is advertised now.
    /// </summary>
    public void Watch(string serverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        bool changed;
        lock (_gate)
        {
            if (_browsing && !_failed && _serverId == serverId)
            {
                return;
            }
            changed = _adverts.Count > 0;
            if (_browsing)
            {
                browser.Stop();
            }
            _adverts.Clear();
            _serverId = serverId;
            _browsing = true;
            _failed = false;
            browser.Start(new Events(this, serverId, ++_generation));
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>No server to watch: the browse stops.</summary>
    public void Unwatch()
    {
        lock (_gate)
        {
            _serverId = null;
            if (!_browsing)
            {
                return;
            }
            _browsing = false;
            _generation++;
            _adverts.Clear();
            browser.Stop();
        }
    }

    /// <summary>
    /// The advertised local-route origins of <paramref name="serverId"/>, at most four: <paramref name="preferred"/> (the
    /// one in use or last confirmed) first when it is advertised, then the rest in the order they came.
    /// </summary>
    public IReadOnlyList<Uri> Origins(string serverId, Uri? preferred = null)
    {
        lock (_gate)
        {
            if (serverId != _serverId)
            {
                return [];
            }
            var origins = OriginsLocked().ToList();
            if (preferred is not null && origins.Remove(preferred))
            {
                origins.Insert(0, preferred);
            }
            return [.. origins.Take(LanAdverts.MaxOrigins)];
        }
    }

    private IEnumerable<Uri> OriginsLocked() => _adverts.Values.OrderBy(a => a.Order).SelectMany(a => a.Origins).Distinct();

    // Browser callbacks, dropped once their browse is over; Changed goes out after the lock.
    private sealed class Events(LanAdvertWatch owner, string serverId, int generation) : ILanBrowseEvents
    {
        public void Resolved(string key, LanAdvertisement advertisement)
        {
            // Another server's advert is not kept at all.
            bool ours = LanAdverts.ServerId(advertisement.TextRecords) == serverId;
            IReadOnlyList<Uri> origins = ours
                ? [.. LanAdverts.Origins(advertisement.Addresses, advertisement.Port).Select(LanAdverts.LocalRouteOrigin).OfType<Uri>()]
                : [];
            Change(adverts =>
            {
                if (origins.Count == 0)
                {
                    adverts.Remove(key);
                }
                else if (adverts.TryGetValue(key, out var known))
                {
                    adverts[key] = (origins, known.Order);
                }
                else if (adverts.Count < MaxAdverts)
                {
                    adverts[key] = (origins, ++owner._order);
                }
            });
        }

        public void Gone(string key) => Change(adverts => adverts.Remove(key));

        public void Failed()
        {
            lock (owner._gate)
            {
                if (owner._generation == generation)
                {
                    // Watch starts it again with the next check.
                    owner._failed = true;
                    owner._logger.LogInformation("Browsing the network for the server's adverts stopped");
                }
            }
        }

        private void Change(Action<Dictionary<string, (IReadOnlyList<Uri> Origins, long Order)>> change)
        {
            bool changed;
            lock (owner._gate)
            {
                if (owner._generation != generation || !owner._browsing)
                {
                    return;
                }
                var before = owner.OriginsLocked().ToList();
                change(owner._adverts);
                changed = !before.SequenceEqual(owner.OriginsLocked());
            }
            if (changed)
            {
                owner.Changed?.Invoke(owner, EventArgs.Empty);
            }
        }
    }
}
