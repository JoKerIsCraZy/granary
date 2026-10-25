using System.Globalization;
using Granary.Core.ViewModels.Player;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI;

namespace Granary.App.Controls;

/// <summary>The Add Subtitles dialog over the player; <see cref="ViewModel"/> is the page's, set before it opens.</summary>
public sealed partial class AddSubtitlesPanel : UserControl
{
    private static readonly string[] Extensions = [".srt", ".vtt", ".ass", ".ssa", ".sub"];

    // The web's results list: at most 60 % of the window, the card within the window.
    private const double ResultsShare = 0.6;
    private const double CardInset = 32;

    private AddSubtitlesViewModel? _viewModel;
    private bool _dragging;
    private bool _pointerOver;

    public AddSubtitlesPanel()
    {
        InitializeComponent();
        Backdrop.SizeChanged += (_, e) =>
        {
            Card.MaxHeight = Math.Max(0, e.NewSize.Height - CardInset);
            ResultsScroller.MaxHeight = e.NewSize.Height * ResultsShare;
        };
    }

    // Null until the page first opens the dialog: x:Bind skips what it cannot reach.
    public AddSubtitlesViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            Bindings.Update();
        }
    }

    /// <summary>Puts the keyboard on the dialog: the search language, as the web focuses it, else Close.</summary>
    public void FocusFirst() => DispatcherQueue.TryEnqueue(() =>
    {
        Control target = _viewModel?.IsOnlineSearchEnabled == true ? SearchLanguageBox : CloseButton;
        target.Focus(FocusState.Programmatic);
    });

    // Instance methods because x:Bind function bindings call them on the control.
    public Visibility Shows(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public bool Not(bool value) => !value;

    // Its own function: one x:Bind function feeding both a bool and a Visibility generates broken code.
    public Visibility Hides(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public string SelectedText(string fileName) => "Selected: " + fileName;

    // A colour badge: the colour at 13 % behind its text, at 33 % round it (the web's ${color}22 and ${color}55).
    public static Brush Fill(string hex) => Tint(hex, 0x22);

    public static Brush Edge(string hex) => Tint(hex, 0x55);

    public static Brush Solid(string hex) => Tint(hex, 0xFF);

    private static SolidColorBrush Tint(string hex, byte alpha)
    {
        uint rgb = uint.Parse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new SolidColorBrush(Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }

    private void OnBackdropTapped(object sender, TappedRoutedEventArgs e) => _viewModel?.CloseCommand.Execute(null);

    // A click on the card is the card's, not the backdrop's.
    private void OnCardTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void OnPanelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            _viewModel?.CloseCommand.Execute(null);
        }
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
        };
        foreach (string extension in Extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }
        if (await picker.PickSingleFileAsync() is { } picked && _viewModel is { } viewModel)
        {
            await viewModel.SelectFileAsync(picked.Path);
        }
    }

    private async void OnResultClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SubtitleSearchRow row } && _viewModel is { } viewModel)
        {
            await viewModel.DownloadAsync(row);
        }
    }

    private void OnDropZoneDragEnter(object sender, DragEventArgs e)
    {
        _dragging = true;
        ShowDropZone();
    }

    private void OnDropZoneDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None;
        e.DragUIOverride.IsCaptionVisible = false;
    }

    private void OnDropZoneDragLeave(object sender, DragEventArgs e)
    {
        _dragging = false;
        ShowDropZone();
    }

    private async void OnDropZoneDrop(object sender, DragEventArgs e)
    {
        _dragging = false;
        ShowDropZone();
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.FirstOrDefault() is StorageFile { Path.Length: > 0 } file && _viewModel is { } viewModel)
        {
            await viewModel.SelectFileAsync(file.Path);
        }
    }

    private void OnDropZonePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = true;
        ShowDropZone();
    }

    private void OnDropZonePointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = false;
        ShowDropZone();
    }

    // The web's dashed edge: white at 20 %, 35 % under the pointer, 50 % while a file is dragged over it.
    private void ShowDropZone()
    {
        byte edge = _dragging ? (byte)0x80 : _pointerOver ? (byte)0x59 : (byte)0x33;
        DropZoneEdge.Stroke = new SolidColorBrush(Color.FromArgb(edge, 0xFF, 0xFF, 0xFF));
        DropZone.Background = new SolidColorBrush(Color.FromArgb(_dragging || _pointerOver ? (byte)0x1A : (byte)0x0D, 0xFF, 0xFF, 0xFF));
        DropZoneTitle.Text = _dragging ? "Drop subtitle file" : "Drag and drop a subtitle file";
    }
}
