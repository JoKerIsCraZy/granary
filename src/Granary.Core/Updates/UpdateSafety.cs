namespace Granary.Core.Updates;

/// <summary>
/// The Velopack settings signed updates rely on (docs/release.md, "Signed releases"). The app hands these to Velopack; a
/// test pins them and that the app uses them.
/// </summary>
public static class UpdateSafety
{
    /// <summary>
    /// Velopack may not apply a package it finds in its packages folder as Granary starts: only one verified as signed is
    /// applied, as Granary exits.
    /// </summary>
    public const bool ApplyPackagesAtStart = false;

    /// <summary>
    /// No delta packages (a negative maximum turns them off): a package rebuilt from deltas is zipped anew, so it is never
    /// the bytes the release signed.
    /// </summary>
    public const int MaximumDeltas = -1;
}
