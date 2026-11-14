using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api;
using Granary.Core.Api.Admin;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Admin;

// A stream's commands, as the web's AdminSessionActions sends them and words their answers.
public sealed partial class AdminActivityViewModel
{
    // Each session's last command sequence: the next one rises above it, as the server applies them in order.
    private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);

    /// <summary>Pause, or Resume for a paused stream. Only for a player that takes live commands.</summary>
    // Concurrent: a command for one stream must not swallow another stream's; a row waits for its own (IsBusy).
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task TogglePauseAsync(AdminSessionRow? row) =>
        row is { CanControl: true } ? SendAsync(row, row.IsPaused ? AdminSessionCommands.Resume : AdminSessionCommands.Pause) : Task.CompletedTask;

    /// <summary>Asks the player to stop; one the server cannot reach is ended by the server shortly after.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task StopAsync(AdminSessionRow? row) => row is null ? Task.CompletedTask : SendAsync(row, AdminSessionCommands.Stop);

    /// <summary>Shows <paramref name="message"/> on the player. Only for a player that takes live commands.</summary>
    public async Task<bool> SendMessageAsync(AdminSessionRow row, string message)
    {
        ArgumentNullException.ThrowIfNull(row);
        string text = message?.Trim() ?? "";
        if (text.Length == 0)
        {
            Say(AppText.Get("web.admin.sessions.playback.toast.messageRequired"), isError: true);
            return false;
        }
        return row.CanControl && await SendAsync(row, AdminSessionCommands.Message, text);
    }

    /// <summary>Ends the session for good: its playback authority is revoked, and the player told as best it can be.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task TerminateAsync(AdminSessionRow? row)
    {
        if (row is null || _session.ActiveServer is not { } server || Waits(row))
        {
            return;
        }
        row.IsBusy = true;
        try
        {
            var receipt = await _admin.TerminateAsync(server.BaseUri, row.SessionId);
            Say(AppText.Get("web.admin.sessions.playback.toast.terminateReceipt",
                ("revoked", receipt.AlreadyRevoked ? "already" : receipt.AuthorityRevoked ? "revoked" : "other"),
                ("notified", receipt.ClientNotified ? "yes" : "other")), isError: false);
            await LoadAsync(manual: false);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not terminate a playback session");
            Say(Failure("terminate", ex), isError: true);
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private async Task<bool> SendAsync(AdminSessionRow row, string action, string? message = null)
    {
        if (_session.ActiveServer is not { } server || Waits(row))
        {
            return false;
        }
        row.IsBusy = true;
        try
        {
            var command = new AdminPlaybackCommand(Guid.NewGuid().ToString(), NextSequence(row.SessionId), message);
            var receipt = await _admin.SendCommandAsync(server.BaseUri, row.SessionId, action, command);
            if (receipt.CommandId != command.CommandId)
            {
                Say(AppText.Get("granary.admin.activity.otherCommand"), isError: true);
                return false;
            }
            bool dispatched = receipt.Delivery == "dispatched";
            if (dispatched && action is AdminSessionCommands.Pause or AdminSessionCommands.Resume)
            {
                row.ExpectPaused(action == AdminSessionCommands.Pause, _time.GetUtcNow());
            }
            Say(Answered(action, dispatched), isError: false);
            if (action == AdminSessionCommands.Stop)
            {
                await LoadAsync(manual: false);
            }
            return true;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not send a playback command");
            Say(Failure(action, ex), isError: true);
            return false;
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    // A stream with a command on its way takes no other until it is answered.
    private bool Waits(AdminSessionRow row)
    {
        if (row.IsBusy)
        {
            Say(AppText.Get("granary.admin.activity.commandWaiting"), isError: true);
        }
        return row.IsBusy;
    }

    // What a command's answer says, as the web's toasts: sent, or the server ends the session in its place.
    private static string Answered(string action, bool dispatched) => (action, dispatched) switch
    {
        (AdminSessionCommands.Message, _) => AppText.Get("web.admin.sessions.playback.toast.messageSent"),
        (AdminSessionCommands.Pause, true) => AppText.Get("web.admin.sessions.playback.toast.pauseSent"),
        (AdminSessionCommands.Pause, false) => AppText.Get("web.admin.sessions.playback.toast.pauseFallback"),
        (AdminSessionCommands.Resume, true) => AppText.Get("web.admin.sessions.playback.toast.resumeSent"),
        (AdminSessionCommands.Resume, false) => AppText.Get("web.admin.sessions.playback.toast.resumeFallback"),
        (_, true) => AppText.Get("web.admin.sessions.playback.toast.stopSent"),
        _ => AppText.Get("web.admin.sessions.playback.toast.stopFallback"),
    };

    // A clock in milliseconds, so two commands never share a sequence, even after the app started again.
    private long NextSequence(string sessionId)
    {
        long next = Math.Max(_sequences.GetValueOrDefault(sessionId) + 1, _time.GetUtcNow().ToUnixTimeMilliseconds());
        _sequences[sessionId] = next;
        return next;
    }

    // The conflict's reason is the server's own words, in English, as the web shows them in English.
    private static string Failure(string action, Exception ex) => ex switch
    {
        SiloApiException { Status: 409 } conflict => AppText.Get("web.admin.sessions.playback.failure.notApplied", ("action", action),
            ("reason", string.IsNullOrWhiteSpace(conflict.Problem.Detail)
                ? AppText.Get("web.admin.sessions.playback.failure.conflictReason")
                : conflict.Problem.Detail)),
        SiloApiException { Code: ProblemCodes.NotFound } or SiloApiException { Status: 404 } =>
            AppText.Get("web.admin.sessions.playback.failure.ended"),
        SiloApiException { Code: ProblemCodes.DependencyUnavailable } => AppText.Get("web.admin.sessions.playback.failure.unavailable"),
        SiloApiException { Status: 401 or 403 } => AppText.Get("granary.admin.activity.controlForbidden"),
        SiloApiException => AppText.Get("web.admin.sessions.playback.failure.generic"),
        _ => LibraryErrors.Describe(ex),
    };
}
