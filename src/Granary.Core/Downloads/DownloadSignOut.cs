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

/// <summary>
/// How many downloads a sign-out or a server's removal takes along, and the bytes their folders take. <see cref="Bytes"/> is
/// null when their size did not come in time, as from a share that does not answer; <see cref="Count"/> is null when not even
/// the downloads could be read in time.
/// </summary>
public sealed record DownloadSummary(int? Count, long? Bytes);

/// <summary>
/// Signing out of a server or removing it takes its downloads along (docs/architecture.md, "Signing out"). The viewer is
/// asked first, with how many go and the space they take, as the Apple app's sign-out asks; the rows, the index entries and
/// the deletes still to send go, and their transfers stop; the server hears the deletes of the active profile's entries, within
/// <see cref="DeleteBudget"/>; then the sign-out, while the folders go beside it. The positions watched from this PC that still
/// wait to be sent (<see cref="OfflineProgressQueue"/>) are sent first where the server takes them (the active profile's,
/// within <see cref="SendBudget"/> of their own), and what could not go is forgotten with the downloads: nothing of what the
/// account watched stays behind. The question says how many titles' progress that is, and is asked for it alone when the
/// account has no downloads here. A drive or a share that does not answer holds the question
/// up for at most <see cref="SummaryBudget"/>, and the sign-out for at most <see cref="StoreBudget"/> more. Only a sign-out the
/// viewer asks for comes here: a sign-in that expired, which the authentication handler ends by itself, keeps the downloads,
/// as the Android app does.
/// </summary>
public sealed class DownloadSignOut(DownloadStore store, DownloadsApi api, ISessionManager session, ICredentialStore credentials,
    IConfirmation confirmation, TimeProvider time, ILogger<DownloadSignOut> logger, OfflineProgressQueue? progress = null,
    Playback.KeptPlaybackSettings? settings = null, OfflineProgressSync? sync = null)
{
    /// <summary>What a question before signing out says when the account signed in is not known: its downloads stay.</summary>
    public const string KeptNote = "Downloads on this PC are kept.";

    /// <summary>The longest the server's deletes may hold a sign-out up; the files go whether it answered or not.</summary>
    public static readonly TimeSpan DeleteBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The longest a question waits for the positions that still wait to be sent, so it can say what is lost, and the longest
    /// the sign-out after it waits for them once more; a server that cannot be reached fails at once, and one that does not
    /// answer holds either up no longer than this.
    /// </summary>
    public static readonly TimeSpan SendBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The longest the question before a sign-out waits for the downloads and their size, as the pages wait for the free space:
    /// a share that does not answer would keep it from coming. Without them it asks all the same.
    /// </summary>
    public static readonly TimeSpan SummaryBudget = DiskSpaceReads.Timeout;

    /// <summary>
    /// The longest a sign-out waits for the downloads to be read, and then once more for what it writes: the index without
    /// them, and what it took out of the positions still to send and the kept playback settings (<see cref="RemovalNote"/>).
    /// What is not done in time is done afterwards, and the sign-out goes on.
    /// </summary>
    public static readonly TimeSpan StoreBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The downloads of a server's account (null: of every account there), on all its profiles, and the size of their folders,
    /// read on the thread pool within <see cref="SummaryBudget"/>. A size that does not come in time is left out, and so is the
    /// count when the downloads themselves cannot be read in time; either is logged without the system's message.
    /// </summary>
    public async Task<DownloadSummary> SummarizeAsync(string serverEntryId, string? accountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverEntryId);
        using var budget = new CancellationTokenSource(SummaryBudget, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        if (!await LoadWithinAsync(SummaryBudget, linked.Token, cancellationToken).ConfigureAwait(false))
        {
            return new DownloadSummary(null, null);
        }
        var downloads = store.All.Where(d => Matches(d.Record.Scope, serverEntryId, accountId)).ToList();
        if (downloads.Count == 0)
        {
            return new DownloadSummary(0, 0);
        }
        var scopes = downloads.Select(d => d.Record.Scope).Distinct().ToList();
        try
        {
            // A read that does not answer runs on by itself.
            long bytes = await Task.Run(() => scopes.Sum(store.BytesOnDisk), CancellationToken.None).WaitAsync(linked.Token).ConfigureAwait(false);
            return new DownloadSummary(downloads.Count, bytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The size of the downloads did not come within {Seconds} s; the question names none", SummaryBudget.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The size of the downloads could not be read ({Failure}); the question names none", DownloadLog.Failure(ex));
        }
        return new DownloadSummary(downloads.Count, null);
    }

    /// <summary>
    /// What a question before signing out or removing a server says of the downloads: "2 downloads (3.0 GB) on this PC are
    /// deleted with it.", without the size when it is not known, and "Any downloads on this PC are deleted with it." when not
    /// even the count is; null when there are none.
    /// </summary>
    public static string? Note(DownloadSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.Count is not { } count)
        {
            return "Any downloads on this PC are deleted with it.";
        }
        if (count <= 0)
        {
            return null;
        }
        string size = summary.Bytes is { } bytes ? " (" + (bytes > 0 ? PrePlayText.FileSize(bytes) : "0 B") + ")" : "";
        return count == 1
            ? $"1 download{size} on this PC is deleted with it."
            : string.Create(CultureInfo.InvariantCulture, $"{count} downloads{size} on this PC are deleted with it.");
    }

    /// <summary>
    /// The account signed in on a server: the session's on the active one, else the one its saved sign-in names; null when
    /// none is known. A sign-out then takes no downloads along (removing the server still takes every account's).
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
    /// What a question before signing out of a server says of the downloads, for the account signed in there
    /// (<see cref="AccountOfAsync"/>): the <see cref="Note"/> of that account's, or, when the account is not known,
    /// <see cref="KeptNote"/> while the server has any here; null when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// For a question that is asked whatever the downloads: with an account known, it also says how many titles' progress
    /// was watched here and is not on the server yet (<see cref="UnsentNote"/>), after one try to send it.
    /// </remarks>
    public async Task<string?> SignOutNoteAsync(string serverEntryId, string? accountId, CancellationToken cancellationToken)
    {
        var summary = await SummarizeAsync(serverEntryId, accountId, cancellationToken).ConfigureAwait(false);
        if (accountId is null)
        {
            return summary.Count is 0 ? null : KeptNote;
        }
        return Join(Note(summary), await UnsentAfterASendAsync(serverEntryId, accountId, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// What the question before removing a server says: the <see cref="Note"/> of every account's downloads there, and how
    /// many titles' progress is not on the server yet (<see cref="UnsentNote"/>), after one try to send it; null when there is
    /// nothing to say.
    /// </summary>
    public async Task<string?> RemoveNoteAsync(string serverEntryId, CancellationToken cancellationToken)
    {
        var summary = await SummarizeAsync(serverEntryId, null, cancellationToken).ConfigureAwait(false);
        return Join(Note(summary), await UnsentAfterASendAsync(serverEntryId, null, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// What a question says of progress watched from this PC that the server does not have: "Progress in 2 titles is not on
    /// your Silo server yet and is lost with it."; null for none.
    /// </summary>
    public static string? UnsentNote(int titles) => titles switch
    {
        <= 0 => null,
        1 => "Progress in 1 title is not on your Silo server yet and is lost with it.",
        _ => string.Create(CultureInfo.InvariantCulture, $"Progress in {titles} titles is not on your Silo server yet and is lost with it."),
    };

    private static string? Join(string? first, string? second) => first is null ? second : second is null ? first : first + " " + second;

    // What still waits after one try to send it, as the question's sentence. The try is the active profile's only: another
    // profile's positions, and another account's or server's, cannot go out as this one.
    private async Task<string?> UnsentAfterASendAsync(string serverEntryId, string? accountId, CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            return null;
        }
        await TrySendAsync(serverEntryId, accountId, SendBudget, cancellationToken).ConfigureAwait(false);
        return UnsentNote(progress.Count(scope => Matches(scope, serverEntryId, accountId)));
    }

    // One send of what the active profile watched here and has not sent, when it is a profile of that server and account and
    // has any. Whatever comes of it, the caller goes on: a server that does not answer costs the budget at most.
    private async Task TrySendAsync(string serverEntryId, string? accountId, TimeSpan budget, CancellationToken cancellationToken)
    {
        if (sync is null || progress is null || DownloadScope.Of(session) is not { } scope || !Matches(scope, serverEntryId, accountId)
            || progress.Count(owner => owner == scope) == 0)
        {
            return;
        }
        using var timeout = new CancellationTokenSource(budget, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await sync.FlushAsync(readBack: false, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("The positions watched from this PC were not sent within {Seconds} s before the sign-out", budget.TotalSeconds);
        }
    }

    /// <summary>
    /// Asks before signing out of <paramref name="server"/> when its account has downloads on this PC, or progress watched
    /// here that could not be sent ("Sign out of NAS?"); true without asking when it has neither, or when the account is not
    /// known (nothing goes then). Call it on the UI thread: the question is shown from there.
    /// </summary>
    public async Task<bool> ConfirmSignOutAsync(ServerEntry server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        return await ConfirmAsync(server, await AccountOfAsync(server.Id, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Takes the downloads of a server's account (null: of every account there) off this PC. First nothing more is kept or
    /// started for them (<see cref="DownloadStore.MarkCleared"/>): a create that answers later keeps nothing, and the queue
    /// starts no transfer and no server work for them until the account is signed in again. Then every matching row, index
    /// entry and delete still to send goes at once, and the queue stops their transfers, so nothing more is written for them.
    /// Then the server hears the deletes of the active profile's entries there, its downloads and the deletes still to send as
    /// they were before, for at most <see cref="DeleteBudget"/>; other profiles' entries cannot be deleted without their
    /// profile, so they stay on the server. It returns once the index without them is written, and the note of what went
    /// out of the positions still to send and the kept playback settings, or after <see cref="StoreBudget"/>; the folders
    /// go after that. When the downloads cannot even be read within
    /// <see cref="StoreBudget"/>, the server hears nothing and they go once they are read; one added after the clear, as by
    /// the same account signed in again meanwhile, stays.
    /// </summary>
    /// <returns>
    /// The clear, to dispose once the sign-out was tried, whatever came of it (<see cref="DownloadStore.EndClear"/>): after
    /// a sign-out that failed, the account's new downloads run again.
    /// </returns>
    public async Task<IDisposable> ClearAsync(string serverEntryId, string? accountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverEntryId);
        // First of all: a create that answers from now on keeps nothing, and the queue starts nothing more for these downloads,
        // until their account is signed in again or the sign-out ends without signing it out.
        var clear = new PendingClear(store, store.MarkCleared(serverEntryId, accountId));
        try
        {
            await TakeOffAsync(clear.Generation, serverEntryId, accountId, cancellationToken).ConfigureAwait(false);
            return clear;
        }
        catch
        {
            clear.Dispose();
            throw;
        }
    }

    // ClearAsync's work once the clear is marked.
    private async Task TakeOffAsync(long generation, string serverEntryId, string? accountId, CancellationToken cancellationToken)
    {
        bool loaded = await LoadWithinAsync(StoreBudget, cancellationToken).ConfigureAwait(false);
        if (loaded)
        {
            // What the active profile watched here and has not sent goes now, while it still can: this sign-in ends next.
            // Within a budget of its own: a server that does not answer it may still take the deletes.
            await TrySendAsync(serverEntryId, accountId, SendBudget, cancellationToken).ConfigureAwait(false);
        }
        // What could not go names titles and times. It could only go out as that account, which is signed out now, or to
        // that server, which is removed; a play still open records nothing more (LocalPlayback).
        progress?.Remove(serverEntryId, accountId);
        // The settings kept for a play without the server have no download left to serve.
        settings?.Remove(serverEntryId, accountId);
        // Both are out for this run at once. On the disk they are once the write each removal queued is through, the note
        // beside the file first; the latest write is that one, or one behind it in the same chain.
        var forgotten = Task.WhenAll(progress?.LatestSave ?? Task.CompletedTask, settings?.LatestSave ?? Task.CompletedTask);
        // What the server is to hear is taken before the rows go: the queue then has no delete of them to send twice.
        var deletes = loaded ? DeletesToSend(serverEntryId, accountId) : null;
        var removal = store.RemoveWhereAsync(scope => Matches(scope, serverEntryId, accountId), generation, CancellationToken.None);
        bool waited = false;
        bool notesWaited = false;
        try
        {
            if (loaded && deletes is not null)
            {
                using var deleteBudget = new CancellationTokenSource(DeleteBudget, time);
                await TellTheServerAsync(deletes, deleteBudget.Token, cancellationToken).ConfigureAwait(false);
            }
            // The writes ran while the server heard the deletes. What is left of them shares one StoreBudget: they do not
            // wait for each other. Downloads that could not be read have no index to wait for; the notes are waited for
            // all the same.
            notesWaited = true;
            await Task.WhenAll(loaded ? WaitForIndexAsync(removal, cancellationToken) : Task.CompletedTask,
                WaitForNotesAsync(forgotten, cancellationToken)).ConfigureAwait(false);
            waited = loaded;
        }
        finally
        {
            // A removal not waited for (the downloads were not read in time, or the server's deletes ended in a failure) logs
            // its own failure.
            if (!waited)
            {
                Observe(removal);
            }
            // The server's deletes ended in a failure: what was taken out is out all the same, and is waited for before
            // that failure goes on, so it is on the disk whatever the caller does next. Not for a caller that gave up.
            if (!notesWaited && !cancellationToken.IsCancellationRequested)
            {
                await WaitForNotesAsync(forgotten, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A sign-out the viewer asked for: asks when something of the account's is lost with it (<see cref="ConfirmSignOutAsync"/>), takes
    /// them along (<see cref="ClearAsync"/>), then signs out of <paramref name="server"/>. A sign-in whose account is not known
    /// takes none along: those on that server are other accounts', as one whose sign-in expired. A sign-out that fails leaves
    /// the account's downloads gone, and its new ones run. False when the viewer said no, or another account was signed in
    /// there while the question was open, and nothing changed; false too when another account was signed in while the
    /// downloads went: they are gone, and that account stays signed in. Call it on the UI thread.
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
        // The question was about this account's downloads: another account signed in there while it was open is left alone.
        if (await AccountOfAsync(server.Id, cancellationToken) != accountId)
        {
            logger.LogInformation("Another account was signed in on server {ServerEntryId} while the sign-out asked; nothing was done", server.Id);
            return false;
        }
        if (accountId is null)
        {
            logger.LogInformation("The account signed in on server {ServerEntryId} is not known; the downloads there stay", server.Id);
            await session.SignOutAsync(server.Id, cancellationToken);
            return true;
        }
        using (await ClearAsync(server.Id, accountId, cancellationToken))
        {
            // The downloads can take a while to go: an account signed in there meanwhile is not the one to sign out.
            if (await AccountOfAsync(server.Id, cancellationToken) != accountId)
            {
                logger.LogInformation("Another account was signed in on server {ServerEntryId} while its downloads went; it stays signed in",
                    server.Id);
                return false;
            }
            await session.SignOutAsync(server.Id, cancellationToken);
        }
        return true;
    }

    // Nothing to lose, no question: no downloads here, and what was watched here went to the server in the try before it.
    // Progress that could not go is asked about also without downloads; it would be lost without a word.
    private async Task<bool> ConfirmAsync(ServerEntry server, string? accountId, CancellationToken cancellationToken)
    {
        if (accountId is null)
        {
            return true;
        }
        string? downloads = Note(await SummarizeAsync(server.Id, accountId, cancellationToken));
        string? note = Join(downloads, await UnsentAfterASendAsync(server.Id, accountId, cancellationToken));
        return note is null || await confirmation.AskAsync($"Sign out of {server.Name}?", note, "Sign out");
    }

    private async Task<bool> LoadWithinAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(budget, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        return await LoadWithinAsync(budget, linked.Token, cancellationToken).ConfigureAwait(false);
    }

    // The store, read within the budget; false when it was not read in time or could not be read, which is logged. A read that
    // does not answer runs on by itself.
    private async Task<bool> LoadWithinAsync(TimeSpan budget, CancellationToken withinBudget, CancellationToken cancellationToken)
    {
        try
        {
            await store.LoadAsync(withinBudget).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The downloads could not be read within {Seconds} s", budget.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The downloads could not be read ({Failure})", DownloadLog.Failure(ex));
        }
        return false;
    }

    // The index without the downloads, waited for within StoreBudget. One that does not come in time, or a follower of the store
    // that failed, does not hold the sign-out up: the downloads are off this PC's lists all the same.
    private async Task WaitForIndexAsync(Task removal, CancellationToken cancellationToken)
    {
        try
        {
            await removal.WaitAsync(StoreBudget, time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("The downloads index was not written within {Seconds} s; the sign-out goes on, and it is written once it can be",
                StoreBudget.TotalSeconds);
            Observe(removal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Taking the downloads off this PC failed in part ({Failure}); the sign-out goes on", DownloadLog.Failure(ex));
        }
    }

    // What the sign-out took out of the positions still to send and of the kept playback settings, waited for within
    // StoreBudget: the note of it beside each file, and the file where it could be written. A Granary that closes right after
    // the sign-out then finds none of it at its next start. Writes that do not come in time do not hold the sign-out up: it
    // is out for this run all the same, and back at the next start only if Granary closes before they get through.
    private async Task WaitForNotesAsync(Task writes, CancellationToken cancellationToken)
    {
        try
        {
            await writes.WaitAsync(StoreBudget, time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("What the sign-out took out of the positions and playback settings kept on this PC was not written within {Seconds} s; the sign-out goes on, and it is written once it can be",
                StoreBudget.TotalSeconds);
            // Not waited for any longer: a failure of theirs is looked at here, so it is never one nobody observed.
            _ = writes.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("What the sign-out took out of the positions and playback settings kept on this PC could not be written ({Failure}); the sign-out goes on",
                DownloadLog.Failure(ex));
        }
    }

    // A removal the sign-out does not wait for logs its failure, if any, rather than leaving it unobserved.
    private void Observe(Task removal) =>
        _ = removal.ContinueWith(
            static (task, state) => ((ILogger)state!).LogWarning("Taking the downloads off this PC failed in part ({Failure})",
                DownloadLog.Failure(task.Exception!.GetBaseException())),
            logger, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    // The active profile's entries of that server and account the server is to hear of, its downloads and the deletes still to
    // send; null when there are none, or the active profile is not of that server and account. The server is read once, and
    // the scope must be that server's: the deletes never go to another server than the one whose downloads they are.
    private ServerDeletes? DeletesToSend(string serverEntryId, string? accountId)
    {
        if (session.ActiveServer is not { } server || server.Id != serverEntryId
            || DownloadScope.Of(session) is not { } scope || scope.ServerEntryId != server.Id || !Matches(scope, serverEntryId, accountId))
        {
            return null;
        }
        var ids = store.In(scope).Select(d => d.Record.DownloadId).Concat(store.PendingDeletes(scope).Select(d => d.DownloadId))
            .Distinct(StringComparer.Ordinal).ToList();
        return ids.Count == 0 ? null : new ServerDeletes(server.BaseUri, scope.ProfileId, ids);
    }

    // The server's deletes, one at a time, as that profile: one the server refuses for itself does not stop the others; no
    // answer, a sign-in it no longer takes, a busy server, a profile switch or the end of the budget stops them all, since the
    // next ones would fare no better.
    private async Task TellTheServerAsync(ServerDeletes deletes, CancellationToken withinBudget, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, withinBudget);
        foreach (string id in deletes.Ids)
        {
            try
            {
                await api.DeleteAsync(deletes.Server, deletes.ProfileId, id, linked.Token).ConfigureAwait(false);
            }
            catch (SiloApiException ex) when (ex.Status is 401 or 429)
            {
                logger.LogInformation("The server took no more deletes before the sign-out ({Status}); the downloads are off this PC all the same",
                    ex.Status);
                return;
            }
            catch (SiloApiException ex)
            {
                logger.LogInformation("The server did not take the delete of download {DownloadId} before the sign-out ({Status})", id, ex.Status);
            }
            catch (Exception ex) when (ex is HttpRequestException or ProfileChangedException or JsonException
                || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                logger.LogInformation("The server could not be told of the deletes before the sign-out ({Failure}); the downloads are off this PC all the same",
                    Describe(ex));
                return;
            }
        }
    }

    /// <summary>The deletes a sign-out sends: the server, the profile they go as, and the entries.</summary>
    private sealed record ServerDeletes(Uri Server, string ProfileId, IReadOnlyList<string> Ids);

    /// <summary>A sign-out's clear while the sign-out is under way; disposing it ends the clear. Twice is no change.</summary>
    private sealed class PendingClear(DownloadStore store, long generation) : IDisposable
    {
        public long Generation { get; } = generation;

        public void Dispose() => store.EndClear(Generation);
    }

    private static bool Matches(DownloadScope scope, string serverEntryId, string? accountId) =>
        scope.ServerEntryId == serverEntryId && (accountId is null || scope.AccountId == accountId);

    // A failure as the log may name it: a network error's kind, or an exception's kind and code, never a message.
    private static string Describe(Exception ex) =>
        ex is HttpRequestException http ? http.HttpRequestError.ToString() : DownloadLog.Failure(ex);
}
