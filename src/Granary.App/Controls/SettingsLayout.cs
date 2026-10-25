using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>
/// A section of a settings page, as Silo's web draws it (SettingsGroup): on the page itself between hairlines, its title
/// and description in a 256 px column on the left, its rows on the right. Its look is the implicit style in
/// Themes/SiloSettings.xaml.
/// </summary>
public sealed partial class SettingsGroup : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingsGroup), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string),
        typeof(SettingsGroup), new PropertyMetadata("", (sender, e) => ((SettingsGroup)sender).DescriptionVisibility =
            string.IsNullOrEmpty(e.NewValue as string) ? Visibility.Collapsed : Visibility.Visible));

    public static readonly DependencyProperty DescriptionVisibilityProperty =
        DependencyProperty.Register(nameof(DescriptionVisibility), typeof(Visibility), typeof(SettingsGroup), new PropertyMetadata(Visibility.Collapsed));

    public SettingsGroup() => IsTabStop = false;

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public Visibility DescriptionVisibility
    {
        get => (Visibility)GetValue(DescriptionVisibilityProperty);
        private set => SetValue(DescriptionVisibilityProperty, value);
    }
}

/// <summary>
/// One setting (the web's SettingRow): its label and description on the left, its control on the right, a hairline
/// above it unless it is the group's first (<c>SiloSettingsFirstRowStyle</c>). The label names the control for screen
/// readers.
/// </summary>
public sealed partial class SettingsRow : ContentControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(SettingsRow),
        new PropertyMetadata("", (sender, _) => ((SettingsRow)sender).NameControl()));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string),
        typeof(SettingsRow), new PropertyMetadata("", (sender, e) =>
        {
            var row = (SettingsRow)sender;
            row.DescriptionVisibility = string.IsNullOrEmpty(e.NewValue as string) ? Visibility.Collapsed : Visibility.Visible;
            row.NameControl();
        }));

    public static readonly DependencyProperty DescriptionVisibilityProperty =
        DependencyProperty.Register(nameof(DescriptionVisibility), typeof(Visibility), typeof(SettingsRow), new PropertyMetadata(Visibility.Collapsed));

    public SettingsRow()
    {
        IsTabStop = false;
        RegisterPropertyChangedCallback(ContentProperty, (_, _) => NameControl());
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public Visibility DescriptionVisibility
    {
        get => (Visibility)GetValue(DescriptionVisibilityProperty);
        private set => SetValue(DescriptionVisibilityProperty, value);
    }

    // The control the row names: set here, and kept in step with the label and the description.
    private DependencyObject? _named;

    // A control without a name of its own is called by its row's label, with the description as its help.
    private void NameControl()
    {
        if (Content is not DependencyObject control)
        {
            return;
        }
        if (!ReferenceEquals(control, _named))
        {
            if (!string.IsNullOrEmpty(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(control)))
            {
                return;
            }
            _named = control;
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, Label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(control, Description ?? "");
    }
}
