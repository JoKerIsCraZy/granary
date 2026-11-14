using Granary.Core.Controller;

namespace Granary.Tests.Controller;

/// <summary>A PlayStation pad as Windows reports it, a plain list of buttons and axes, read by button position.</summary>
public sealed class SonyPadTests
{
    // At rest: both sticks in the middle, both triggers out. Axes by usage (the triggers before the right stick's Y), and
    // as the pad's report lists them (the right stick's Y first).
    private static readonly double[] RestByUsage = [0.5, 0.5, 0.5, 0, 0, 0.5];
    private static readonly double[] RestByReport = [0.5, 0.5, 0.5, 0.5, 0, 0];

    private readonly SonyPad _pad = new();

    private static bool[] Buttons(params int[] pressed)
    {
        bool[] buttons = new bool[14];
        foreach (int index in pressed)
        {
            buttons[index] = true;
        }
        return buttons;
    }

    [Theory]
    [InlineData(0x05C4)]
    [InlineData(0x09CC)]
    [InlineData(0x0BA0)]
    [InlineData(0x0CE6)]
    [InlineData(0x0DF2)]
    public void DualShock4AndDualSensePads_AreKnown(int productId)
    {
        Assert.True(SonyPad.IsKnown(0x054C, productId));
    }

    [Theory]
    [InlineData(0x054C, 0x0268)]
    [InlineData(0x045E, 0x0CE6)]
    public void OtherDevices_AreLeftAlone(int vendorId, int productId)
    {
        Assert.False(SonyPad.IsKnown(vendorId, productId));
    }

    [Theory]
    [InlineData(0, PadButtons.X)]
    [InlineData(1, PadButtons.A)]
    [InlineData(2, PadButtons.B)]
    [InlineData(3, PadButtons.Y)]
    [InlineData(4, PadButtons.LeftShoulder)]
    [InlineData(5, PadButtons.RightShoulder)]
    [InlineData(6, PadButtons.LeftTrigger)]
    [InlineData(7, PadButtons.RightTrigger)]
    [InlineData(8, PadButtons.View)]
    [InlineData(9, PadButtons.Menu)]
    [InlineData(10, PadButtons.LeftStick)]
    [InlineData(11, PadButtons.RightStick)]
    public void EachButton_CountsByWhereItSits(int index, PadButtons expected)
    {
        var reading = _pad.Read(Buttons(index), RestByUsage, PadButtons.None);

        Assert.Equal(expected, reading.Buttons);
    }

    [Fact]
    public void ThePlayStationButtonAndTheTouchpad_DoNothing()
    {
        var reading = _pad.Read(Buttons(12, 13), RestByUsage, PadButtons.None);

        Assert.Equal(PadButtons.None, reading.Buttons);
    }

    [Fact]
    public void TheDPad_ComesWithTheButtons()
    {
        var reading = _pad.Read(Buttons(1), RestByUsage, PadButtons.DPadUp | PadButtons.DPadRight);

        Assert.Equal(PadButtons.A | PadButtons.DPadUp | PadButtons.DPadRight, reading.Buttons);
    }

    [Fact]
    public void APadAtRest_ReadsAsRest()
    {
        Assert.Equal(default, _pad.Read(Buttons(), RestByUsage, PadButtons.None));
        Assert.Equal(default, new SonyPad().Read(Buttons(), RestByReport, PadButtons.None));
    }

    [Fact]
    public void TheLeftStick_RunsRightAndUpPositive()
    {
        var reading = _pad.Read(Buttons(), [1, 0, 0.5, 0, 0, 0.5], PadButtons.None);

        Assert.Equal(1, reading.LeftX);
        Assert.Equal(1, reading.LeftY);
    }

    [Fact]
    public void AxesListedByUsage_GiveTheRightStickAndBothTriggers()
    {
        _pad.Read(Buttons(), RestByUsage, PadButtons.None);

        var reading = _pad.Read(Buttons(6, 7), [0.5, 0.5, 0, 0.4, 1, 1], PadButtons.None);

        Assert.Equal(-1, reading.RightX);
        Assert.Equal(-1, reading.RightY);
        Assert.Equal(0.4, reading.LeftTrigger);
        Assert.Equal(1, reading.RightTrigger);
    }

    [Fact]
    public void AxesListedAsThePadSendsThem_GiveTheSame()
    {
        _pad.Read(Buttons(), RestByReport, PadButtons.None);

        var reading = _pad.Read(Buttons(6, 7), [0.5, 0.5, 0, 1, 0.4, 1], PadButtons.None);

        Assert.Equal(-1, reading.RightX);
        Assert.Equal(-1, reading.RightY);
        Assert.Equal(0.4, reading.LeftTrigger);
        Assert.Equal(1, reading.RightTrigger);
    }

    [Fact]
    public void UntilTheOrderOfTheAxesIsKnown_TheRightStickRestsAndATriggerCountsAsPulledHome()
    {
        // Both orders fit: the triggers are pressed, so neither axis can be told from the stick.
        var reading = _pad.Read(Buttons(7), [0.5, 0.5, 0.5, 0.2, 1, 0.8], PadButtons.None);

        Assert.Equal(0, reading.RightY);
        Assert.Equal(0, reading.LeftTrigger);
        Assert.Equal(1, reading.RightTrigger);
        Assert.True(reading.Buttons.HasFlag(PadButtons.RightTrigger));
    }

    [Fact]
    public void AStickPushedAllTheWayUpAtTheFirstReading_DecidesNothingYet()
    {
        // The right stick's Y reads 0 at the top, as a trigger at rest does.
        var first = _pad.Read(Buttons(), [0.5, 0.5, 0.5, 0, 0, 0], PadButtons.None);
        var atRest = _pad.Read(Buttons(), RestByReport, PadButtons.None);
        var down = _pad.Read(Buttons(), [0.5, 0.5, 0.5, 1, 0, 0], PadButtons.None);

        Assert.Equal(0, first.RightY);
        Assert.Equal(0, atRest.RightY);
        Assert.Equal(-1, down.RightY);
    }

    [Fact]
    public void APadWithFewerAxesOrButtonsThanExpected_IsReadAsFarAsItGoes()
    {
        var reading = _pad.Read([false, true], [1, 0.5], PadButtons.None);

        Assert.Equal(new PadReading(PadButtons.A, LeftX: 1), reading);
    }

    [Fact]
    public void AShortReading_AfterTheOrderOfTheAxesIsKnown_IsReadAsFarAsItGoes()
    {
        _pad.Read(Buttons(), RestByUsage, PadButtons.None);

        var reading = _pad.Read([false, true, false, false, false, false, false, true], [1, 0.5], PadButtons.None);

        // The right stick rests and the pressed trigger counts as pulled home, as before the order was known.
        Assert.Equal(new PadReading(PadButtons.A | PadButtons.RightTrigger, LeftX: 1, RightTrigger: 1), reading);
    }
}
