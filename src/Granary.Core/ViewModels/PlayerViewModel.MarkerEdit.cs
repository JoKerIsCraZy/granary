using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Player;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

// The web player's Edit markers: for an account with marker_edit, or an admin acting on the primary profile, a button
// opens the marker panel; while it is open the seek bar shows the draft with handles for the chosen kind.
public sealed partial class PlayerViewModel
{
    private readonly AccountPermissions? _permissions;

    // Read once per page, when the first server title starts: an answer per page is enough to offer the button.
    private Task? _permissionRead;
    private bool _mayEditMarkers;

    /// <summary>The marker panel's state; its saves go to the playback that plays.</summary>
    public MarkerEditorViewModel MarkerEditor { get; private set; } = null!;

    /// <summary>The Edit markers button is there: the account may, and a server file with a length plays.</summary>
    [ObservableProperty]
    public partial bool CanEditMarkers { get; private set; }

    /// <summary>What the seek bar shows: the draft while the markers are edited, else the saved ones.</summary>
    public IReadOnlyList<PlaybackMarker> SeekBarMarkers => MarkerEditor.IsEditing ? MarkerEditor.DraftMarkers : Markers;

    /// <summary>Opens the marker panel on the saved markers, or closes it and drops the draft.</summary>
    [RelayCommand]
    private void ToggleMarkerEdit()
    {
        if (MarkerEditor.IsEditing)
        {
            MarkerEditor.Cancel();
        }
        else if (CanEditMarkers)
        {
            MarkerEditor.Begin(Markers, DurationSeconds);
        }
    }

    /// <summary>"Set start" and "Set end": the chosen kind's edge to where the viewer is.</summary>
    public void SetMarkerEdgeHere(string kind, MarkerEdge edge) => MarkerEditor.SetEdge(kind, edge, PositionSeconds);

    private void InitializeMarkerEdit()
    {
        MarkerEditor = new MarkerEditorViewModel(changes =>
            _playback is { } playback ? playback.SaveMarkersAsync(changes) : throw new InvalidOperationException("No title plays."));
        MarkerEditor.PropertyChanged += OnMarkerEditorPropertyChanged;
    }

    private void OnMarkerEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MarkerEditorViewModel.IsEditing) or nameof(MarkerEditorViewModel.DraftMarkers))
        {
            OnPropertyChanged(nameof(SeekBarMarkers));
        }
    }

    partial void OnMarkersChanged(IReadOnlyList<PlaybackMarker> value) => OnPropertyChanged(nameof(SeekBarMarkers));

    private void ReadMarkerPermission()
    {
        if (_permissions is not { } permissions || _permissionRead is not null)
        {
            return;
        }
        _permissionRead = ReadAsync();

        async Task ReadAsync()
        {
            try
            {
                _mayEditMarkers = await permissions.HasAsync(AccountPermissions.MarkerEdit);
            }
            catch (Exception ex)
            {
                // Only the button depends on it.
                _logger.LogError(ex, "Checking the marker permission failed");
            }
            ShowMarkerEdit();
        }
    }

    private void ShowMarkerEdit() =>
        CanEditMarkers = _mayEditMarkers && _playback is not null && DurationSeconds > 0 && SubtitleFileId is not null;

    // Another title: the draft was for the file before.
    private void ResetMarkerEdit()
    {
        MarkerEditor.Cancel();
        CanEditMarkers = false;
    }
}
