using System.Windows.Input;
using Granary.Core.ViewModels.Detail;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>The Extras section of a title's page: its groups of extras, each card playing its extra through <see cref="PlayCommand"/>.</summary>
public sealed partial class DetailExtrasSection : UserControl
{
    public static readonly DependencyProperty GroupsProperty =
        DependencyProperty.Register(nameof(Groups), typeof(object), typeof(DetailExtrasSection), new PropertyMetadata(null));

    public static readonly DependencyProperty PlayCommandProperty =
        DependencyProperty.Register(nameof(PlayCommand), typeof(ICommand), typeof(DetailExtrasSection), new PropertyMetadata(null));

    public DetailExtrasSection()
    {
        InitializeComponent();
    }

    /// <summary>The <see cref="DetailExtraGroup"/>s in their order.</summary>
    public object? Groups
    {
        get => GetValue(GroupsProperty);
        set => SetValue(GroupsProperty, value);
    }

    /// <summary>Plays the <see cref="DetailExtra"/> it is given.</summary>
    public ICommand? PlayCommand
    {
        get => (ICommand?)GetValue(PlayCommandProperty);
        set => SetValue(PlayCommandProperty, value);
    }

    private void OnExtraClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DetailExtra extra } && PlayCommand is { } command && command.CanExecute(extra))
        {
            command.Execute(extra);
        }
    }
}
