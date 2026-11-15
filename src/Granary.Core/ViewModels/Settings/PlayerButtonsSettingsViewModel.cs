using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Playback;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Playback → Player buttons: which buttons the right of the player's bar shows and in which order, for this
/// profile on this PC (<see cref="PlayerButtonsLayout"/>), as Settings → Navigation &amp; Cards → Sidebar arranges the
/// sidebar. Granary's own: Silo's clients have no such editor. An open player, the minimized one too, follows each change at once.
/// </summary>
public sealed partial class PlayerButtonsSettingsViewModel : ObservableObject, IDisposable
{
    private readonly PlayerButtonsLayout _layout;

    public PlayerButtonsSettingsViewModel(PlayerButtonsLayout layout)
    {
        _layout = layout;
        Rows = [.. PlayerBar.Buttons.Select(info => new PlayerButtonRowViewModel(this, info))];
        _layout.PropertyChanged += OnLayoutChanged;
        Show();
    }

    /// <summary>A row for each button, in the profile's order; the rows stay the same objects while they move.</summary>
    public ObservableCollection<PlayerButtonRowViewModel> Rows { get; }

    /// <summary>The bar is not today's; Reset brings that back.</summary>
    public bool IsCustomized => _layout.IsCustomized;

    [RelayCommand]
    private Task ResetAsync() => _layout.ResetAsync();

    internal Task MoveAsync(PlayerButton button, int delta) => _layout.MoveAsync(button, delta);

    internal Task SetShownAsync(PlayerButton button, bool shown)
    {
        int revision = _layout.Revision;
        var saving = _layout.SetShownAsync(button, shown);
        if (_layout.Revision == revision)
        {
            // Nothing changed (no profile acting): the switch goes back to what the bar shows.
            Show();
        }
        return saving;
    }

    public void Dispose() => _layout.PropertyChanged -= OnLayoutChanged;

    private void OnLayoutChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerButtonsLayout.Revision))
        {
            Show();
        }
        else if (e.PropertyName == nameof(PlayerButtonsLayout.IsCustomized))
        {
            OnPropertyChanged(nameof(IsCustomized));
        }
    }

    private void Show()
    {
        var bar = _layout.Current;
        var rows = bar.Order.Select(button => Rows.First(row => row.Button == button)).ToList();
        for (int index = 0; index < rows.Count; index++)
        {
            rows[index].Show(bar.Shows(rows[index].Button), canMoveUp: index > 0, canMoveDown: index < rows.Count - 1);
        }
        ObservableRows.Sync(Rows, rows);
        OnPropertyChanged(nameof(IsCustomized));
    }
}

/// <summary>A button of the player's bar: shown or not, and one place earlier or later.</summary>
public sealed partial class PlayerButtonRowViewModel : ObservableObject
{
    private readonly PlayerButtonsSettingsViewModel _owner;
    private readonly PlayerButtonInfo _info;
    // Set while the row shows the saved bar, so showing it changes nothing.
    private bool _showing;

    internal PlayerButtonRowViewModel(PlayerButtonsSettingsViewModel owner, PlayerButtonInfo info)
    {
        _owner = owner;
        _info = info;
    }

    public PlayerButton Button => _info.Button;

    public string Label => Button switch
    {
        PlayerButton.Volume => AppText.Get("web.player.controls.volume"),
        PlayerButton.SleepTimer => AppText.Get("web.player.sleepTimer.trigger"),
        PlayerButton.Audio => AppText.Get("web.player.audioMenu.trigger"),
        PlayerButton.Subtitles => AppText.Get("granary.settings.playerButtons.subtitles"),
        PlayerButton.Chapters => AppText.Get("web.player.chaptersMenu.trigger"),
        PlayerButton.Settings => AppText.Get("granary.settings.playerButtons.settings"),
        PlayerButton.EditMarkers => AppText.Get("web.player.controls.editMarkers"),
        PlayerButton.PlaybackInfo => AppText.Get("web.player.controls.playbackInfo"),
        PlayerButton.MiniView => AppText.Get("granary.settings.playerButtons.miniView"),
        PlayerButton.Fill => AppText.Get("web.player.controls.fillScreen"),
        PlayerButton.FullScreen => AppText.Get("granary.settings.playerButtons.fullScreen"),
        _ => _info.Label,
    };

    /// <summary>When it shows, or what it holds; null when there is nothing to say.</summary>
    public string? Note => Button switch
    {
        PlayerButton.Volume => AppText.Get("granary.settings.playerButtons.volumeNote"),
        PlayerButton.SleepTimer => AppText.Get("granary.settings.playerButtons.sleepTimerNote"),
        PlayerButton.Audio => AppText.Get("granary.settings.playerButtons.audioNote"),
        PlayerButton.Subtitles => AppText.Get("granary.settings.playerButtons.subtitlesNote"),
        PlayerButton.Chapters => AppText.Get("granary.settings.playerButtons.chaptersNote"),
        PlayerButton.Settings => AppText.Get("granary.settings.playerButtons.settingsNote"),
        PlayerButton.EditMarkers => AppText.Get("granary.settings.playerButtons.editMarkersNote"),
        PlayerButton.FullScreen => AppText.Get("granary.settings.playerButtons.fullScreenNote"),
        _ => null,
    };

    [ObservableProperty]
    public partial bool IsShown { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    public partial bool CanMoveUp { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    public partial bool CanMoveDown { get; private set; }

    // The move shows at once and the save goes on by itself, so the button takes the next press at once: a held key or a
    // remote's quick presses all count.
    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => _ = _owner.MoveAsync(Button, -1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => _ = _owner.MoveAsync(Button, 1);

    internal void Show(bool shown, bool canMoveUp, bool canMoveDown)
    {
        _showing = true;
        try
        {
            IsShown = shown;
        }
        finally
        {
            _showing = false;
        }
        CanMoveUp = canMoveUp;
        CanMoveDown = canMoveDown;
    }

    partial void OnIsShownChanged(bool value)
    {
        if (!_showing)
        {
            _ = _owner.SetShownAsync(Button, value);
        }
    }
}
