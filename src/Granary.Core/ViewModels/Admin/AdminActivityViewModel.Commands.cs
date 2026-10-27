using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api;
using Granary.Core.Api.Admin;
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
            Say("Message is required", isError: true);
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
            string revoked = receipt.AlreadyRevoked ? "Playback authority was already revoked"
                : receipt.AuthorityRevoked ? "Playback authority revoked"
                : "Playback authority was not revoked";
            string notified = receipt.ClientNotified
                ? "the player was told to stop"
                : "the player could not be reached and will stop when it next contacts the server";
            Say($"{revoked}; {notified}.", isError: false);
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
                Say("The server answered a different command.", isError: true);
                return false;
            }
            bool dispatched = receipt.Delivery == "dispatched";
            if (dispatched && action is AdminSessionCommands.Pause or AdminSessionCommands.Resume)
            {
                row.ExpectPaused(action == AdminSessionCommands.Pause, _time.GetUtcNow());
            }
            Say(action switch
            {
                AdminSessionCommands.Message => "Message sent",
                _ when !dispatched => $"{Capitalized(action)} could not reach the player directly. Silo will end the session shortly instead.",
                _ => $"{Capitalized(action)} command sent",
            }, isError: false);
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
            Say("A command for this stream is still on its way. Try again in a moment.", isError: true);
        }
        return row.IsBusy;
    }

    // A clock in milliseconds, so two commands never share a sequence, even after the app started again.
    private long NextSequence(string sessionId)
    {
        long next = Math.Max(_sequences.GetValueOrDefault(sessionId) + 1, _time.GetUtcNow().ToUnixTimeMilliseconds());
        _sequences[sessionId] = next;
        return next;
    }

    private static string Failure(string action, Exception ex) => ex switch
    {
        SiloApiException { Status: 409 } conflict =>
            $"The {action} command was not applied: {(string.IsNullOrWhiteSpace(conflict.Problem.Detail) ? "the session state changed" : conflict.Problem.Detail)}",
        SiloApiException { Code: ProblemCodes.NotFound } or SiloApiException { Status: 404 } => "This playback session has already ended.",
        SiloApiException { Code: ProblemCodes.DependencyUnavailable } => "Playback control is unavailable on this server.",
        SiloApiException { Status: 401 or 403 } => "This account can't control playback sessions.",
        SiloApiException => "Failed to send session command",
        _ => LibraryErrors.Describe(ex),
    };

    private static string Capitalized(string action) => char.ToUpperInvariant(action[0]) + action[1..];
}
