using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>
/// Granary started while its server cannot be reached (docs/architecture.md, "Offline start"): the remembered profile's
/// downloads open instead of Home, as Silo's Android app opens its downloads, and the Downloads page says so with a Retry.
/// Only the start asks: a profile without downloads on this PC, and a start without a remembered profile, start as ever,
/// and losing the server while Granary runs moves nobody. The server is asked what it lets the profile download, the call
/// the downloads make anyway; a problem of its own means it is there, also a refusal. A Retry that finds it says so
/// (<see cref="ServerRecovery"/>), so what waited for the server goes at once.
/// </summary>
public sealed partial class OfflineStart(DownloadStore store, DownloadsApi api, ISessionManager session, TimeProvider time,
    ILogger<OfflineStart> logger, ServerRecovery? recovery = null) : ObservableObject
{
    /// <summary>
    /// How long the start waits in all, for the downloads to be read from the disk and then for the server's answer: the
    /// start screen stays up meanwhile, and gives way after 6 s whatever it waits for. A Retry has as long for its answer.
    /// </summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();

    // Whose start found no server; the note is about that server and profile only.
    private DownloadScope? _offlineFor;

    /// <summary>
    /// The start found no server, and no <see cref="RetryAsync"/> has reached it since. It changes on any thread, under a
    /// lock: who listens must only pass it on.
    /// </summary>
    [ObservableProperty]
    public partial bool IsOffline { get; private set; }

    /// <summary>
    /// Whether the start opens the downloads: the session is ready with a remembered profile, that profile has downloads
    /// on this PC, and the server could not be reached within <see cref="Budget"/>. It never throws: whatever goes wrong,
    /// Granary starts as without it.
    /// </summary>
    public async Task<bool> CheckAtStartAsync(CancellationToken cancellationToken)
    {
        if (DownloadScope.Of(session) is not { } scope || session.ActiveServer is not { } server)
        {
            return false;
        }
        try
        {
            // One budget for the read and the question: what the read took, the server has less.
            using var budget = new CancellationTokenSource(Budget, time);
            using var within = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, cancellationToken);
            if (!await HasDownloadsAsync(scope, within.Token, cancellationToken).ConfigureAwait(false)
                || await IsReachableAsync(server.BaseUri, scope.ProfileId, within.Token, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
            return MarkOffline(scope);
        }
        catch (Exception ex)
        {
            // A file system error's message names the full path, with the Windows user name: only its kind is logged.
            logger.LogWarning("Whether Granary starts without its server could not be found out ({Failure})", DownloadLog.Failure(ex));
            return false;
        }
    }

    /// <summary>
    /// Asks the server again, within <see cref="Budget"/>. True once it answers for the server and profile the start was
    /// for: the note goes, and the caller opens Home. False while it still cannot be reached, and when there is nothing
    /// to ask for (any more).
    /// </summary>
    public async Task<bool> RetryAsync(CancellationToken cancellationToken)
    {
        DownloadScope? scope;
        lock (_gate)
        {
            scope = _offlineFor;
        }
        if (scope is null)
        {
            return false;
        }
        if (DownloadScope.Of(session) != scope || session.ActiveServer is not { } server)
        {
            Clear(scope);
            return false;
        }
        bool reachable;
        try
        {
            using var budget = new CancellationTokenSource(Budget, time);
            using var within = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, cancellationToken);
            reachable = await IsReachableAsync(server.BaseUri, scope.ProfileId, within.Token, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The server could not be asked again ({Failure})", DownloadLog.Failure(ex));
            return false;
        }
        // Another server or profile since the question went out: the session took the note away, and the shell has routed.
        if (!reachable || !Clear(scope))
        {
            return false;
        }
        recovery?.Report("Retry");
        return true;
    }

    // The index is in the data folder, but its load also looks into the downloads folder, which can be a drive that sleeps
    // or a share that does not answer: the start goes on without the downloads then.
    private async Task<bool> HasDownloadsAsync(DownloadScope scope, CancellationToken within, CancellationToken cancellationToken)
    {
        try
        {
            // The load itself goes on beside: it is the store's, and the pages wait for it too.
            await store.LoadAsync(CancellationToken.None).WaitAsync(within).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The downloads on this PC were not read within {Seconds} s; Granary starts without looking for its server",
                (int)Budget.TotalSeconds);
            return false;
        }
        return store.In(scope).Count > 0;
    }

    // Reachable is a server that answers, also with a refusal, as long as the refusal is a problem it wrote: Home then says
    // what it said. Not reachable: no connection, no answer within the budget (within), an answer that is no Silo
    // server's (a network's sign-in page, an error page without a problem, whatever its status), and a gateway or a tunnel
    // answering in the server's place.
    private async Task<bool> IsReachableAsync(Uri server, string profileId, CancellationToken within, CancellationToken cancellationToken)
    {
        var asked = api.GetCapabilityAsync(server, profileId, within);
        try
        {
            // Also ends a request that does not let go when it is cancelled.
            await asked.WaitAsync(within).ConfigureAwait(false);
            return true;
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation("The server could not be reached ({Failure})", ApiFailureText.Describe(ex));
            return false;
        }
        catch (SiloApiException ex)
        {
            logger.LogInformation("The answer to whether the server is there was {Status}, {Kind}", ex.Status,
                ex.HasProblem ? "a problem of the server's" : "not a problem of the server's");
            return ex.HasProblem && !IsGatewayStatus(ex.Status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The budget, or the connection's own timeout. Whatever the request still answers, nobody waits for it.
            _ = asked.ContinueWith(static late => _ = late.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            logger.LogInformation("The server did not answer in time");
            return false;
        }
    }

    // What a reverse proxy or a tunnel answers when the server behind it is down, as Silo's Android app tells them
    // (PlaybackSessionLifecycle.kt, isGatewayOrTunnelFailureStatus): 502, 503, 504, a tunnel's 520 to 527, and 530.
    private static bool IsGatewayStatus(int status) => status is 502 or 503 or 504 or (>= 520 and <= 527) or 530;

    // Follows the session from here on, so a sign-out or another profile takes the note away. False when that already
    // happened while the server was asked.
    private bool MarkOffline(DownloadScope scope)
    {
        lock (_gate)
        {
            if (_offlineFor is null)
            {
                session.Changed += OnSessionChanged;
            }
            _offlineFor = scope;
            IsOffline = true;
        }
        if (DownloadScope.Of(session) != scope)
        {
            Clear(scope);
            return false;
        }
        logger.LogInformation("Granary started without its server; the downloads open");
        return true;
    }

    // True when the note was still that scope's.
    private bool Clear(DownloadScope scope)
    {
        lock (_gate)
        {
            if (_offlineFor != scope)
            {
                return false;
            }
            _offlineFor = null;
            session.Changed -= OnSessionChanged;
            IsOffline = false;
            return true;
        }
    }

    // Any thread. An account update of the same server and profile changes nothing.
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        DownloadScope? scope;
        lock (_gate)
        {
            scope = _offlineFor;
        }
        if (scope is not null && DownloadScope.Of(session) != scope)
        {
            Clear(scope);
        }
    }
}
