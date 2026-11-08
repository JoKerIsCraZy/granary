using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>
/// One star of <see cref="StarRating"/>: a button, and to screen readers a radio button that is selected when it is the
/// rating, as the web's star is a radio with <c>aria-checked</c>.
/// </summary>
internal sealed partial class RatingStar(int number) : Button
{
    public int Number { get; } = number;

    /// <summary>The star is the rating.</summary>
    public bool IsSelected { get; private set; }

    internal void SetSelected(bool isSelected)
    {
        if (IsSelected == isSelected)
        {
            return;
        }
        IsSelected = isSelected;
        if (FrameworkElementAutomationPeer.FromElement(this) is RatingStarAutomationPeer peer)
        {
            peer.RaiseSelectedChanged(!isSelected, isSelected);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RatingStarAutomationPeer(this);
}

/// <summary><see cref="RatingStar"/> as a radio button; selecting it rates, and selecting the rating's star changes nothing.</summary>
internal sealed partial class RatingStarAutomationPeer(RatingStar owner) : ButtonAutomationPeer(owner), ISelectionItemProvider
{
    private RatingStar Star => (RatingStar)Owner;

    public bool IsSelected => Star.IsSelected;

    public IRawElementProviderSimple? SelectionContainer => null;

    // A click on the rating's star clears it: selecting it again must not.
    public void Select()
    {
        if (!Star.IsSelected)
        {
            Invoke();
        }
    }

    public void AddToSelection() => Select();

    // A radio is unselected by selecting another.
    public void RemoveFromSelection()
    {
    }

    public void RaiseSelectedChanged(bool previous, bool current)
    {
        if (ListenerExists(AutomationEvents.PropertyChanged))
        {
            RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, previous, current);
        }
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.RadioButton;

    protected override string GetClassNameCore() => nameof(RatingStar);

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.SelectionItem ? this : base.GetPatternCore(patternInterface);
}
