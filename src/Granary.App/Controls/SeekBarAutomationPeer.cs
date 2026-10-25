using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Granary.App.Controls;

/// <summary>
/// <see cref="SeekBar"/> as a slider for screen readers: its range is the title in seconds, and the value text
/// says the position in words ("4 minutes 5 seconds of 1 hour 30 minutes"). Setting the value seeks.
/// </summary>
internal sealed partial class SeekBarAutomationPeer(SeekBar owner)
    : FrameworkElementAutomationPeer(owner), IRangeValueProvider, IValueProvider
{
    private SeekBar Bar => (SeekBar)Owner;

    public double Minimum => 0;

    public double Maximum => Bar.Duration;

    public double SmallChange => Bar.BackStepSeconds;

    public double LargeChange => Bar.ForwardStepSeconds;

    double IRangeValueProvider.Value => Bar.DisplayedSeconds;

    string IValueProvider.Value => Bar.Duration > 0
        ? $"{PlayerTime.Spoken(Bar.DisplayedSeconds)} of {PlayerTime.Spoken(Bar.Duration)}"
        : "";

    bool IRangeValueProvider.IsReadOnly => !Bar.CanSeek;

    // The text is derived from the position; changes go through the range value.
    bool IValueProvider.IsReadOnly => true;

    public void SetValue(double value) => Bar.RequestSeek(value);

    public void SetValue(string value) => throw new InvalidOperationException("The seek bar's value text is read-only.");

    /// <summary>Tells screen readers about a seek the user made, so the new time is read out.</summary>
    public void RaiseValueChanged(double previous, double current)
    {
        if (ListenerExists(AutomationEvents.PropertyChanged))
        {
            RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, previous, current);
        }
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

    protected override string GetClassNameCore() => nameof(SeekBar);

    protected override string GetNameCore() => base.GetNameCore() is { Length: > 0 } name ? name : "Seek";

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface is PatternInterface.RangeValue or PatternInterface.Value ? this : base.GetPatternCore(patternInterface);
}
