using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>
/// The fold button of a sidebar section: <see cref="Command"/> (with <see cref="CommandParameter"/>) folds or opens it,
/// <see cref="IsExpanded"/> says which it is. Screen readers hear "Collapse Discover" or "Expand Discover", as on the web.
/// </summary>
public sealed partial class SidebarSectionHeader : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(SidebarSectionHeader), new PropertyMetadata("", (sender, _) => ((SidebarSectionHeader)sender).Show()));

    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(SidebarSectionHeader), new PropertyMetadata(true, (sender, _) => ((SidebarSectionHeader)sender).Show()));

    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(SidebarSectionHeader), new PropertyMetadata(false, (sender, _) => ((SidebarSectionHeader)sender).Show()));

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(SidebarSectionHeader), new PropertyMetadata(null));

    public static readonly DependencyProperty CommandParameterProperty =
        DependencyProperty.Register(nameof(CommandParameter), typeof(object), typeof(SidebarSectionHeader), new PropertyMetadata(null));

    // The open pane's header is as tall as the web's label with its gap; the rail's divider takes less.
    private const double OpenHeight = 40;
    private const double CompactHeight = 24;

    // Segoe Fluent Icons' ChevronDown and ChevronRight.
    private const string OpenGlyph = "";
    private const string FoldedGlyph = "";

    public SidebarSectionHeader()
    {
        InitializeComponent();
        Show();
    }

    /// <summary>"Discover": shown in capitals, read as written.</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>In the icon-only rail: a divider instead of the fold button.</summary>
    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    // Not the button's own Command: a command over an enum cannot run while its parameter is still unset, and the button
    // would stay disabled once the parameter arrived.
    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (Command is { } command && command.CanExecute(CommandParameter))
        {
            command.Execute(CommandParameter);
        }
    }

    private void Show()
    {
        Text.Text = Label.ToUpperInvariant();
        Chevron.Glyph = IsExpanded ? OpenGlyph : FoldedGlyph;
        AutomationProperties.SetName(Toggle, $"{(IsExpanded ? "Collapse" : "Expand")} {Label}");
        Toggle.Visibility = IsCompact ? Visibility.Collapsed : Visibility.Visible;
        Divider.Visibility = IsCompact ? Visibility.Visible : Visibility.Collapsed;
        Height = IsCompact ? CompactHeight : OpenHeight;
    }
}
