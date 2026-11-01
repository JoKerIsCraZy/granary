using System.ComponentModel;
using System.Numerics;
using Granary.App.Controls;
using Granary.Core.ViewModels;
using Microsoft.UI.Xaml;

namespace Granary.App.Views;

// While the stream is planned anew (PlayerViewModel.PlanChange): Granary's mark turns at the bottom right, where the skip
// pill sits, above the pill or the next episode's card when one shows and above the bar while the controls show. The
// settings button says "…" meanwhile; on a narrow page it shows only its gear, as the web's below its sm breakpoint.
public sealed partial class PlayerPage
{
    public static readonly DependencyProperty IsNarrowProperty =
        DependencyProperty.Register(nameof(IsNarrow), typeof(bool), typeof(PlayerPage), new PropertyMetadata(false));

    // The web's sm breakpoint.
    private const double NarrowWidth = 640;

    // Between the mark and the pill or card under it.
    private const double PlanChangeMarkGap = 12;

    /// <summary>The page is narrower than the web's sm breakpoint: the settings button leaves out its label.</summary>
    public bool IsNarrow
    {
        get => (bool)GetValue(IsNarrowProperty);
        private set => SetValue(IsNarrowProperty, value);
    }

    private void InitializePlanChange()
    {
        SizeChanged += (_, e) => IsNarrow = e.NewSize.Width < NarrowWidth;
        SkipPrompt.SizeChanged += (_, _) => PlaceSkipPrompt();
        NextUpCard.SizeChanged += (_, _) => PlaceSkipPrompt();
        // The view model is the page's own and goes with it.
        ViewModel.PropertyChanged += OnPlanChangePropertyChanged;
    }

    private void OnPlanChangePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.PlanChange):
                // The mark appears with its name, so a live region would not speak yet: the change is read out.
                if (ViewModel.IsChangingPlan && !IsCompact)
                {
                    Announcer.Announce(this, ViewModel.PlanChangeText, "PlanChange");
                }
                PlaceSkipPrompt();
                break;
            case nameof(PlayerViewModel.HasSkipPrompt) or nameof(PlayerViewModel.IsNextUpCardShown):
                PlaceSkipPrompt();
                break;
        }
    }

    // As far up as the pill (lift), and above the pill or the card when one shows: they share the corner.
    private void PlacePlanChangeMark(double lift)
    {
        double under = Math.Max(Stacked(SkipPrompt, ViewModel.HasSkipPrompt), Stacked(NextUpCard, ViewModel.IsNextUpCardShown));
        PlanChangeMark.Translation = new Vector3(0, -(float)(lift + under), 0);
    }

    private static double Stacked(FrameworkElement element, bool isShown) =>
        isShown && element.ActualHeight > 0 ? element.ActualHeight + PlanChangeMarkGap : 0;

    // Instance methods because x:Bind function bindings call them on the page.
    public Visibility ShowsSettingsLabel(string label, bool isNarrow) =>
        label.Length > 0 && !isNarrow ? Visibility.Visible : Visibility.Collapsed;
}
