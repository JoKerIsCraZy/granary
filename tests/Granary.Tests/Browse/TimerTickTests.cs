using System.Text.RegularExpressions;

namespace Granary.Tests.Browse;

/// <summary>
/// The app's timers tick through <c>WeakTick</c>, with static lambdas. A timer is not a XAML object, so the collector
/// cannot see that a Tick handler holding its control is a cycle (control, timer, handler, control): a page with such a
/// timer stayed in memory for good once the frame let it go, and every full collection walked it again. A lambda that
/// captures its control makes the same cycle through <c>WeakTick</c>.
/// </summary>
public sealed partial class TimerTickTests
{
    private const string Helper = "src/Granary.App/Controls/WeakTick.cs";

    // Files that subscribe to Tick themselves, and why that holds nothing.
    private static readonly string[] Allowed =
    [
        Helper,
        "src/Granary.App/Controls/ScrollMotion.cs", // one static timer, whose handler holds no control
        "src/Granary.App/MainWindow.xaml.cs", // the window's own timers: it lives as long as the app
    ];

    [Fact]
    public void TimerTicks_GoThroughWeakTick()
    {
        var found = Lines()
            .Where(line => !Allowed.Contains(line.File) && TickSubscription().IsMatch(line.Text))
            .Select(line => line.Where)
            .ToList();

        Assert.Empty(found);
    }

    [Fact]
    public void WeakTicks_TakeStaticLambdas()
    {
        var found = Lines()
            .Where(line => line.File != Helper && WeakTickCall().IsMatch(line.Text) && !StaticLambda().IsMatch(line.Text))
            .Select(line => line.Where)
            .ToList();

        Assert.Empty(found);
    }

    [Theory]
    [InlineData("        _timer.Tick += (_, _) => Refresh();", true)]
    [InlineData("        _clock.Tick += OnClockTick;", true)]
    [InlineData("            .Tick += OnClockTick;", true)]
    [InlineData("        // _timer.Tick += (_, _) => Refresh();", false)]
    [InlineData("        WeakTick.Subscribe(_timer, this, static view => view.Refresh());", false)]
    public void ASubscription_IsFound_OutsideAComment(string line, bool isFound)
    {
        Assert.Equal(isFound, TickSubscription().IsMatch(line));
    }

    [Theory]
    [InlineData("        WeakTick.Subscribe(_timer, this, static view => view.Refresh());", true)]
    [InlineData("        WeakTick.Subscribe(_timer, this, static view =>", true)]
    [InlineData("        WeakTick.Subscribe(_timer, this, view => Refresh());", false)]
    [InlineData("        WeakTick.Subscribe(_timer, this, tick);", false)]
    public void AWeakTick_TakesAStaticLambda(string line, bool isStatic)
    {
        Assert.Matches(WeakTickCall(), line);
        Assert.Equal(isStatic, StaticLambda().IsMatch(line));
    }

    private static IEnumerable<(string File, string Text, string Where)> Lines() =>
        from file in RepositoryFiles.Under("src/Granary.App", "*.cs")
        let relative = RepositoryFiles.Relative(file)
        from line in File.ReadLines(file).Select((text, index) => (Text: text, Number: index + 1))
        select (relative, line.Text, $"{relative}:{line.Number}");

    [GeneratedRegex(@"^(?!\s*//).*\.Tick\s*\+=")]
    private static partial Regex TickSubscription();

    [GeneratedRegex(@"^(?!\s*//).*\bWeakTick\.Subscribe\(")]
    private static partial Regex WeakTickCall();

    [GeneratedRegex(@"\bWeakTick\.Subscribe\([^,]+,[^,]+,\s*static\s")]
    private static partial Regex StaticLambda();
}
