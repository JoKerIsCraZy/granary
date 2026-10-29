using Granary.Core.ViewModels.Player;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Granary.App.Views;

// The sync indicator's look, as the web's SubtitleSyncIndicator: a white edge while a job runs, emerald for a success, sky
// for a note, amber for a warning, each with its icon. The brushes are made once per page.
public sealed partial class PlayerPage
{
    private readonly SolidColorBrush _syncRunningEdge = new(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _syncSuccessEdge = new(Color.FromArgb(0x66, 0x34, 0xD3, 0x99));
    private readonly SolidColorBrush _syncInfoEdge = new(Color.FromArgb(0x66, 0x38, 0xBD, 0xF8));
    private readonly SolidColorBrush _syncWarningEdge = new(Color.FromArgb(0x80, 0xFB, 0xBF, 0x24));
    private readonly SolidColorBrush _syncSuccessIcon = new(Color.FromArgb(0xFF, 0x6E, 0xE7, 0xB7));
    private readonly SolidColorBrush _syncInfoIcon = new(Color.FromArgb(0xFF, 0x7D, 0xD3, 0xFC));
    private readonly SolidColorBrush _syncWarningIcon = new(Color.FromArgb(0xFF, 0xFC, 0xD3, 0x4D));

    public Brush SyncToneBorder(SubtitleSyncTone tone) => tone switch
    {
        SubtitleSyncTone.Success => _syncSuccessEdge,
        SubtitleSyncTone.Info => _syncInfoEdge,
        SubtitleSyncTone.Warning => _syncWarningEdge,
        _ => _syncRunningEdge,
    };

    public Brush SyncToneForeground(SubtitleSyncTone tone) => tone switch
    {
        SubtitleSyncTone.Success => _syncSuccessIcon,
        SubtitleSyncTone.Warning => _syncWarningIcon,
        _ => _syncInfoIcon,
    };

    // Segoe Fluent Icons: Completed, Warning, Info.
    public string SyncToneGlyph(SubtitleSyncTone tone) => tone switch
    {
        SubtitleSyncTone.Success => "\uE930",
        SubtitleSyncTone.Warning => "\uE7BA",
        _ => "\uE946",
    };
}
