using Microsoft.Extensions.Logging;

namespace Granary.Core.Servers;

/// <summary>
/// The <c>_silo._tcp</c> adverts of the active server, for its local route (server-identity.md, rule 1): adverts whose
/// TXT <c>id</c> is the server's identity, each reduced to HTTPS on a private IPv4 address and its port. They are only
/// candidates: <see cref="LocalRouteMonitor"/> uses one only when it presents the pinned key and answers as the server.
/// Browses while <see cref="Watch"/> names a server, as Apple's <c>LocalRouteController</c> does.
/// </summary>
public sealed class LanAdvertWatch(ILanServiceBrowser browser, ILogger<LanAdvertWatch> logger)
{
    private readonly Lock _gate = new();
    private readonly ILogger<LanAdvertWatch> _logger = logger;

    // Every advert seen, whatever its id: the watched server can change without a new browse.
    private readonly Dictionary<string, (string ServerId, IReadOnlyList<Uri> Origins)> _adverts = new(StringComparer.Ordinal);
    private string? _serverId;
    private bool _browsing;
    private bool _failed;
    private int _generation;

    /// <summary>Raised on any thread when the watched server's advertised origins changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Browses for <paramref name="serverId"/>'s adverts; a browse that stopped on its own starts again.</summary>
    public void Watch(string serverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        bool changed;
        lock (_gate)
        {
            var before = OriginsOf(_serverId);
            _serverId = serverId;
            if (!_browsing || _failed)
            {
                if (_browsing)
                {
                    browser.Stop();
                }
                _adverts.Clear();
                _browsing = true;
                _failed = false;
                browser.Start(new Events(this, ++_generation));
            }
            changed = !before.SequenceEqual(OriginsOf(_serverId));
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

    /// <summary>The advertised local-route origins of <paramref name="serverId"/>, at most four.</summary>
    public IReadOnlyList<Uri> Origins(string serverId)
    {
        lock (_gate)
        {
            return OriginsOf(serverId);
        }
    }

    private IReadOnlyList<Uri> OriginsOf(string? serverId) => serverId is null
        ? []
        :
        [
            .. _adverts.Values
                .Where(a => a.ServerId == serverId)
                .SelectMany(a => a.Origins)
                .Distinct()
                .OrderBy(o => o.AbsoluteUri, StringComparer.Ordinal)
                .Take(LanAdverts.MaxOrigins),
        ];

    // Browser callbacks, dropped once their browse is over; Changed goes out after the lock.
    private sealed class Events(LanAdvertWatch owner, int generation) : ILanBrowseEvents
    {
        public void Resolved(string key, LanAdvertisement advertisement)
        {
            string? serverId = LanAdverts.ServerId(advertisement.TextRecords);
            IReadOnlyList<Uri> origins =
            [
                .. LanAdverts.Origins(advertisement.Addresses, advertisement.Port).Select(LanAdverts.LocalRouteOrigin).OfType<Uri>(),
            ];
            Change(adverts =>
            {
                if (serverId is null || origins.Count == 0)
                {
                    adverts.Remove(key);
                }
                else
                {
                    adverts[key] = (serverId, origins);
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

        private void Change(Action<Dictionary<string, (string ServerId, IReadOnlyList<Uri> Origins)>> change)
        {
            bool changed;
            lock (owner._gate)
            {
                if (owner._generation != generation || !owner._browsing)
                {
                    return;
                }
                var before = owner.OriginsOf(owner._serverId);
                change(owner._adverts);
                changed = !before.SequenceEqual(owner.OriginsOf(owner._serverId));
            }
            if (changed)
            {
                owner.Changed?.Invoke(owner, EventArgs.Empty);
            }
        }
    }
}
