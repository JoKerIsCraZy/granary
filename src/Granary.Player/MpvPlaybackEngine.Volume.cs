using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Granary.Player;

// The viewer's volume and mute over the profiles of their mpv.conf (docs/architecture.md, "Volume"). The player sets the
// viewer's kept ones as each title starts, after mpv read the file, so they win over a volume or mute in it; but a profile
// can still set either later: an extension or protocol profile as the file opens, a profile-cond one while it plays. Once
// Granary has set one (SetVolume, SetMuted), a profile's other value is set back, as KeepsOwn does for Granary's own
// options. Unlike those it is not a matter of safety: a profile that keeps changing it has its way for the rest of the
// title after SetBackLimit set-backs, and the file stays in use. Before Granary sets one, mpv.conf has its say.
public sealed partial class MpvPlaybackEngine
{
    private const string VolumeOption = "volume";
    private const string MuteOption = "mute";

    // On mpv's event thread, for a notice of the volume or the mute while mpv runs with a mpv.conf. True when it is not to
    // be shown: a profile's value, being set back, or one older than Granary's latest, which a newer notice follows. A
    // notice without a value, or a value mpv cannot give, leaves nothing to set back.
    private bool KeepsViewersSound(string name, object? value)
    {
        if (name is not (VolumeOption or MuteOption) || value is null || _kept is null || Volatile.Read(ref _failingClosed) != 0)
        {
            return false;
        }
        long title = TitleNow();
        int times;
        lock (_globalsGate)
        {
            // All under the gate, which the viewer's own changes take too: what mpv holds now is weighed against the
            // latest, a notice older than that is no profile's, and only a set-back that is still needed is counted and
            // made, so a change of the viewer's in between never uses up one of SetBackLimit.
            if (!_globals.TryGetValue(name, out string? own) || SameSound(name, value, own))
            {
                return false;
            }
            string? now = _mpv.GetString(name);
            if (now is null)
            {
                return false;
            }
            if (SameSound(name, now, own))
            {
                return true;
            }
            times = CountSetBack(name, title);
            if (times <= SetBackLimit)
            {
                _mpv.SetProperty(name, own);
            }
        }
        if (times == 1)
        {
            _logger.LogWarning("mpv.conf set {Option}; Granary keeps the viewer's", name);
        }
        else if (times == SetBackLimit + 1)
        {
            _logger.LogWarning("mpv.conf keeps changing {Option}; it stays as the file sets it for this title", name);
        }
        return times <= SetBackLimit;
    }

    // mpv reports the volume as a number and the mute as a flag, and gives both as text when read; Granary sets text.
    private static bool SameSound(string name, object? value, string own) => name == VolumeOption
        ? Level(value) is { } level && Level(own) is { } ownLevel && Math.Abs(level - ownLevel) < 0.01
        : Flag(value) is { } flag && flag == Flag(own);

    private static double? Level(object? value) => value switch
    {
        double level => level,
        string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double level) => level,
        _ => null,
    };

    private static bool? Flag(object? value) => value switch
    {
        bool flag => flag,
        "yes" => true,
        "no" => false,
        _ => null,
    };
}
