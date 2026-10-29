using Granary.Core.ViewModels.Player;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Granary.App.Views;

// The sync indicator's look, as the web's SubtitleSyncIndicator: a white edge while a job runs, emerald for a success, sky
// for a note, amber for a warning, each with its icon.
public sealed partial class PlayerPage
{
    public Brush SyncToneBorder(SubtitleSyncTone tone) => new SolidColorBrush(tone switch
    {
        SubtitleSyncTone.Success => Color.FromArgb(0x66, 0x34, 0xD3, 0x99),
        SubtitleSyncTone.Info => Color.FromArgb(0x66, 0x38, 0xBD, 0xF8),
        SubtitleSyncTone.Warning => Color.FromArgb(0x80, 0xFB, 0xBF, 0x24),
        _ => Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
    });

    public Brush SyncToneForeground(SubtitleSyncTone tone) => new SolidColorBrush(tone switch
    {
        SubtitleSyncTone.Success => Color.FromArgb(0xFF, 0x6E, 0xE7, 0xB7),
        SubtitleSyncTone.Warning => Color.FromArgb(0xFF, 0xFC, 0xD3, 0x4D),
        _ => Color.FromArgb(0xFF, 0x7D, 0xD3, 0xFC),
    });

    // Segoe Fluent Icons: Completed, Warning, Info.
    public string SyncToneGlyph(SubtitleSyncTone tone) => tone switch
    {
        SubtitleSyncTone.Success => "\uE930",
        SubtitleSyncTone.Warning => "\uE7BA",
        _ => "\uE946",
    };
}
