using System.ComponentModel;
using System.Globalization;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Admin;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Views.Admin;

/// <summary>
/// An account's own policy fields, the Access or the Limits half (<see cref="ShowsLimits"/>), on an
/// <see cref="AdminPolicyForm"/>. The note of a server-wide download switch links to the web's Downloads settings on the
/// server (<see cref="Server"/>).
/// </summary>
public sealed partial class AdminPolicyFieldsView : UserControl
{
    public static readonly DependencyProperty PolicyProperty = DependencyProperty.Register(nameof(Policy), typeof(AdminPolicyForm),
        typeof(AdminPolicyFieldsView), new PropertyMetadata(null, OnPolicyChanged));

    public AdminPolicyFieldsView()
    {
        InitializeComponent();
    }

    public AdminPolicyForm Policy
    {
        get => (AdminPolicyForm?)GetValue(PolicyProperty) ?? Empty;
        set => SetValue(PolicyProperty, value);
    }

    /// <summary>The Limits tab's half; otherwise the Access tab's.</summary>
    public bool ShowsLimits
    {
        get => LimitsPart.Visibility == Visibility.Visible;
        set
        {
            LimitsPart.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            AccessPart.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>The server whose web app the download note links to.</summary>
    public Uri? Server { get; set; }

    // Until a form is given, an empty one keeps the bindings fed.
    private static AdminPolicyForm Empty { get; } = new();

    /// <summary>A library's kind as the web's selector shows it: the server's word with a capital, as its CSS capitalize.</summary>
    public static string Kind(string type) =>
        type.Length == 0 ? "" : char.ToUpper(type[0], CultureInfo.CurrentCulture) + type[1..];

    public static string OverrideName(string label) => $"{label}: {AppText.Get("web.profiles.userPolicy.override")}";

    public static GridLength MenuWidth(bool custom) => custom ? new GridLength(128) : new GridLength(1, GridUnitType.Star);

    public static GridLength BoxWidth(bool custom) => custom ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

    public static Visibility Has(string text) => text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private static void OnPolicyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (AdminPolicyFieldsView)sender;
        if (e.OldValue is AdminPolicyForm old)
        {
            old.PropertyChanged -= view.OnPolicyPropertyChanged;
        }
        if (e.NewValue is AdminPolicyForm form)
        {
            form.PropertyChanged += view.OnPolicyPropertyChanged;
        }
        view.Bindings.Update();
        view.ShowNotice();
    }

    private void OnPolicyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AdminPolicyForm.DownloadsNoticeKey))
        {
            ShowNotice();
        }
    }

    private void ShowNotice()
    {
        if (Policy.DownloadsNoticeKey is { } key)
        {
            AdminInlines.Fill(DownloadsNotice, key, () => AdminWebLinks.Open(Server, AdminWebLinks.DownloadsSettings));
        }
    }
}
