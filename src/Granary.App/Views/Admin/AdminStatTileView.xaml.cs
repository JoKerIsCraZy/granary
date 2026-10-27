using Granary.Core.ViewModels.Admin;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Views.Admin;

public sealed partial class AdminStatTileView : UserControl
{
    public static readonly DependencyProperty TileProperty = DependencyProperty.Register(
        nameof(Tile), typeof(AdminStatTile), typeof(AdminStatTileView), new PropertyMetadata(new AdminStatTile("", "", ""), OnTileChanged));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(AdminStatTileView), new PropertyMetadata(""));

    public AdminStatTileView() => InitializeComponent();

    public AdminStatTile Tile
    {
        get => (AdminStatTile)GetValue(TileProperty);
        set => SetValue(TileProperty, value);
    }

    /// <summary>The tile's icon, a Segoe Fluent Icons glyph.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    // One name for the reader: "Total Movies: 1,990, 2,048 files".
    private static void OnTileChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var tile = (AdminStatTile)e.NewValue;
        AutomationProperties.SetName(d, tile.IsUnavailable ? $"{tile.Label}: unavailable" : $"{tile.Label}: {tile.Value}, {tile.Sub}");
    }
}
