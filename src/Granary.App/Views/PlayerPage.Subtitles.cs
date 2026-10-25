using System.ComponentModel;
using Granary.Core.Api.Subtitles;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Player;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace Granary.App.Views;

// The subtitle menu's footer, as on the web: Add Subtitles… opens the dialog for the file that plays, Appearance… the
// look. While either is open the player's keys and the fading controls wait.
public sealed partial class PlayerPage
{
    // Made when first opened, then kept: the languages chosen stay while the page plays.
    private AddSubtitlesViewModel? _addSubtitles;

    private bool IsPanelOpen => AddSubtitlesPanel.Visibility == Visibility.Visible || ViewModel.IsAppearanceOpen;

    private void InitializeSubtitlePanels()
    {
        AppearancePanel.ViewModel = ViewModel;
        ViewModel.PropertyChanged += OnSubtitlePanelsPropertyChanged;
    }

    private void StopSubtitlePanels()
    {
        ViewModel.PropertyChanged -= OnSubtitlePanelsPropertyChanged;
        if (_addSubtitles is { } addSubtitles)
        {
            addSubtitles.CloseRequested -= OnAddSubtitlesCloseRequested;
            addSubtitles.Added -= OnSubtitleAdded;
        }
    }

    private void OnAddSubtitlesClick(object sender, RoutedEventArgs e)
    {
        SubtitleMenu.Hide();
        if (ViewModel.SubtitleFileId is not { } fileId)
        {
            return;
        }
        if (_addSubtitles is null)
        {
            _addSubtitles = App.Services.GetRequiredService<AddSubtitlesViewModel>();
            _addSubtitles.CloseRequested += OnAddSubtitlesCloseRequested;
            _addSubtitles.Added += OnSubtitleAdded;
            AddSubtitlesPanel.ViewModel = _addSubtitles;
        }
        _addSubtitles.Open(fileId);
        AddSubtitlesPanel.Visibility = Visibility.Visible;
        ShowControls();
        AddSubtitlesPanel.FocusFirst();
    }

    private void OnAppearanceClick(object sender, RoutedEventArgs e)
    {
        SubtitleMenu.Hide();
        ViewModel.OpenAppearanceCommand.Execute(null);
        AppearancePanel.FocusFirst();
    }

    private void OnAddSubtitlesCloseRequested(object? sender, EventArgs e) => CloseAddSubtitles();

    // The new subtitle shows once a plan lists it; the stream reloads where the viewer is.
    private void OnSubtitleAdded(object? sender, StoredSubtitle subtitle) => ViewModel.ShowAddedSubtitle(subtitle.Id);

    private void CloseAddSubtitles()
    {
        if (AddSubtitlesPanel.Visibility == Visibility.Collapsed)
        {
            return;
        }
        AddSubtitlesPanel.Visibility = Visibility.Collapsed;
        KeepFocusInPage();
    }

    private void OnSubtitlePanelsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // Another title: the dialog was for the file before.
            case nameof(PlayerViewModel.HasSubtitleMenu) when !ViewModel.HasSubtitleMenu:
                CloseAddSubtitles();
                break;
            case nameof(PlayerViewModel.IsAppearanceOpen) when !ViewModel.IsAppearanceOpen:
                KeepFocusInPage();
                break;
        }
    }
}
