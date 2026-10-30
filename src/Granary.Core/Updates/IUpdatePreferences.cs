namespace Granary.Core.Updates;

/// <summary>Whether Granary updates itself on its own (Settings → About → Automatic updates). The app's, not a server's or a profile's.</summary>
public interface IUpdatePreferences
{
    /// <summary>
    /// On (the default), Granary looks for updates at the start and every six hours, downloads them and installs them when it
    /// closes. Off, it looks only when asked and installs only once the user chooses to.
    /// </summary>
    bool IsAutomatic { get; }

    /// <summary>Changes the choice and keeps it for the next start. Never fails: a save that does not work is logged.</summary>
    Task SetAutomaticAsync(bool automatic);
}
