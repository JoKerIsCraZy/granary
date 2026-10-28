namespace Granary.Core.Abstractions;

/// <summary>
/// A question asked before something that can't be taken back, as the web's confirm dialogs: the viewer picks the
/// action or Cancel.
/// </summary>
public interface IConfirmation
{
    /// <summary>True when the viewer picked <paramref name="action"/>; false for Cancel, or when nothing could be asked.</summary>
    Task<bool> AskAsync(string title, string message, string action);
}
