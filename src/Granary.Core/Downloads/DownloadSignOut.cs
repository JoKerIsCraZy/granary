using System.Globalization;
using System.Text.Json;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>How many downloads a sign-out or a server's removal takes along, and the bytes their folders take.</summary>
public sealed record DownloadSummary(int Count, long Bytes);

/// <summary>
/// Signing out of a server or removing it takes its downloads along (docs/architecture.md, "Signing out"). The viewer is
/// asked first, with how many go and the space they take, as the Apple app's sign-out asks; the server hears the deletes
/// of the active profile's entries, within <see cref="DeleteBudget"/>; then the files, the index entries and the deletes
/// still to send go; then the sign-out. Only a sign-out the viewer asks for comes here: a sign-in that expired, which the
/// authentication handler ends by itself, keeps the downloads, as the Android app does.
/// </summary>
public sealed class DownloadSignOut(DownloadStore store, DownloadsApi api, ISessionManager session, ICredentialStore credentials,
    IConfirmation confirmation, TimeProvider time, ILogger<DownloadSignOut> logger)
{
    /// <summary>The longest the server's deletes may hold a sign-out up; the files go whether it answered or not.</summary>
    public static readonly TimeSpan DeleteBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The downloads of a server's account (null: of every account there), on all its profiles. It reads the size of every
    /// folder from the disk: never call it on the UI thread (<see cref="SummarizeAsync"/>).
    /// </summary>
    public DownloadSummary Summarize(string serverEntryId, string? accountId)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverEntryId);
        var downloads = store.All.Where(d => Matches(d.Record.Scope, serverEntryId, accountId)).ToList();
        long bytes = downloads.Select(d => d.Record.Scope).Distinct().Sum(store.BytesOnDisk);
        return new DownloadSummary(downloads.Count, bytes);
    }

    /// <summary>As <see cref="Summarize"/>, once the store is loaded, on the thread pool.</summary>
    public async Task<DownloadSummary> SummarizeAsync(string serverEntryId, string? accountId, CancellationToken cancellationToken)
    {
        await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Summarize(serverEntryId, accountId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What a question before signing out or removing a server says of the downloads: "2 downloads (3.0 GB) on this PC are
    /// deleted with it."; null when there are none.
    /// </summary>
    public static string? Note(DownloadSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.Count <= 0)
        {
            return null;
        }
        string size = summary.Bytes > 0 ? PrePlayText.FileSize(summary.Bytes) : "0 B";
        return summary.Count == 1
            ? $"1 download ({size}) on this PC is deleted with it."
            : string.Create(CultureInfo.InvariantCulture, $"{summary.Count} downloads ({size}) on this PC are deleted with it.");
    }

    /// <summary>
    /// The account signed in on a server: the session's on the active one, else the one its saved sign-in names; null when
    /// none is known, which stands for every account there.
    /// </summary>
    public async Task<string?> AccountOfAsync(string serverEntryId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverEntryId);
        if (session.ActiveServer?.Id == serverEntryId && session.Account is { } account)
        {
            return account.Id;
        }
        return (await credentials.GetAsync(serverEntryId, cancellationToken).ConfigureAwait(false))?.Account?.Id;
    }

    /// <summary>
    /// Asks before signing out of <paramref name="server"/> when its account has downloads on this PC ("Sign out of NAS?");
    /// true without asking when it has none. Call it on the UI thread: the question is shown from there.
    /// </summary>
    public async Task<bool> ConfirmSignOutAsync(ServerEntry server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        return await ConfirmAsync(server, await AccountOfAsync(server.Id, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Takes the downloads of a server's account (null: of every account there) off this PC. The server first hears the
    /// deletes of the active profile's entries there, its downloads and the deletes still to send, for at most
    /// <see cref="DeleteBudget"/>; other profiles' entries cannot be deleted without their profile, so they stay on the server.
    /// Then every matching folder, index entry and delete still to send goes, whether the server answered or not.
    /// </summary>
    public async Task ClearAsync(string serverEntryId, string? accountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverEntryId);
        await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        await TellTheServerAsync(serverEntryId, accountId, cancellationToken).ConfigureAwait(false);
        await store.RemoveWhereAsync(scope => Matches(scope, serverEntryId, accountId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A sign-out the viewer asked for: asks when the account has downloads here (<see cref="ConfirmSignOutAsync"/>), takes
    /// them along (<see cref="ClearAsync"/>), then signs out of <paramref name="server"/>. False when the viewer said no, and
    /// nothing changed. Call it on the UI thread.
    /// </summary>
    public async Task<bool> SignOutAsync(ServerEntry server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        // One read of the account, so the downloads asked about are the ones that go.
        string? accountId = await AccountOfAsync(server.Id, cancellationToken);
        if (!await ConfirmAsync(server, accountId, cancellationToken))
        {
            return false;
        }
        await ClearAsync(server.Id, accountId, cancellationToken);
        await session.SignOutAsync(server.Id, cancellationToken);
        return true;
    }

    private async Task<bool> ConfirmAsync(ServerEntry server, string? accountId, CancellationToken cancellationToken) =>
        Note(await SummarizeAsync(server.Id, accountId, cancellationToken)) is not { } note
        || await confirmation.AskAsync($"Sign out of {server.Name}?", note, "Sign out");

    // The server's deletes of the active profile's entries of that server and account, one at a time: one the server
    // refuses does not stop the others; no answer, a profile switch or the end of the budget stops them all.
    private async Task TellTheServerAsync(string serverEntryId, string? accountId, CancellationToken cancellationToken)
    {
        if (DownloadScope.Of(session) is not { } scope || !Matches(scope, serverEntryId, accountId) || session.ActiveServer is not { } server)
        {
            return;
        }
        var ids = store.In(scope).Select(d => d.Record.DownloadId).Concat(store.PendingDeletes(scope).Select(d => d.DownloadId))
            .Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return;
        }
        using var budget = new CancellationTokenSource(DeleteBudget, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        foreach (string id in ids)
        {
            try
            {
                await api.DeleteAsync(server.BaseUri, scope.ProfileId, id, linked.Token).ConfigureAwait(false);
            }
            catch (SiloApiException ex)
            {
                logger.LogInformation("The server did not take the delete of download {DownloadId} before the sign-out ({Status})", id, ex.Status);
            }
            catch (Exception ex) when (ex is HttpRequestException or ProfileChangedException or JsonException
                || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                logger.LogInformation("The server could not be told of the deletes before the sign-out ({Failure}); the files go all the same",
                    Describe(ex));
                return;
            }
        }
    }

    private static bool Matches(DownloadScope scope, string serverEntryId, string? accountId) =>
        scope.ServerEntryId == serverEntryId && (accountId is null || scope.AccountId == accountId);

    // A failure as the log may name it: a network error's kind, or an exception's kind and code, never a message.
    private static string Describe(Exception ex) =>
        ex is HttpRequestException http ? http.HttpRequestError.ToString() : DownloadLog.Failure(ex);
}
