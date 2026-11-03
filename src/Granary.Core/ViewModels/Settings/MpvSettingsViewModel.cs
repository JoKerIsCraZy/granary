using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Playback → This PC, below "mpv configuration": what Granary's own editor changed (Granary's own; Silo's
/// clients have no player configuration). The page disposes it as it leaves.
/// </summary>
public sealed partial class MpvSettingsViewModel : ObservableObject, IDisposable
{
    public const string PendingText = "Your changes to mpv.conf apply after you restart Granary.";

    private readonly MpvConfigChanges _changes;

    public MpvSettingsViewModel(MpvConfigChanges changes)
    {
        _changes = changes;
        _changes.Changed += OnChanged;
        ShowChanges();
    }

    /// <summary>That mpv.conf changed since the player read it; empty otherwise.</summary>
    [ObservableProperty]
    public partial string ChangeText { get; private set; } = "";

    public void Dispose() => _changes.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs e) => ShowChanges();

    private void ShowChanges() => ChangeText = _changes.IsPending ? PendingText : "";
}
