using System.Text.RegularExpressions;

namespace Granary.Tests.Browse;

/// <summary>
/// The app's timers tick through <c>WeakTick</c>, with static lambdas. A timer is not a XAML object, so the collector
/// cannot see that a Tick handler holding its control is a cycle (control, timer, handler, control): a page with such a
/// timer stayed in memory for good once the frame let it go, and every full collection walked it again. A lambda that
/// captures its control makes the same cycle through <c>WeakTick</c> (and <c>WeakCallback</c> refuses it when it runs).
/// </summary>
public sealed partial class TimerTickTests
{
    private const string Helper = "src/Granary.App/Controls/WeakTick.cs";

    // Files that subscribe to Tick themselves, and why that holds nothing.
    private static readonly string[] Allowed =
    [
        Helper,
        "src/Granary.App/Controls/ScrollMotion.cs", // a static class: its one timer can hold no control
        "src/Granary.App/MainWindow.xaml.cs", // the window's own timers: it lives as long as the app
    ];

    [Fact]
    public void TimerTicks_GoThroughWeakTick()
    {
        var found = Sources()
            .Where(source => !Allowed.Contains(source.File))
            .SelectMany(source => Where(source, TickSubscription()))
            .ToList();

        Assert.Empty(found);
    }

    [Fact]
    public void WeakTicks_TakeStaticLambdas()
    {
        var found = Sources()
            .Where(source => source.File != Helper)
            .SelectMany(source => Where(source, NonStaticWeakTick()))
            .ToList();

        Assert.Empty(found);
    }

    [Theory]
    [InlineData("_timer.Tick += (_, _) => Refresh();", true)]
    [InlineData("_clock.Tick += OnClockTick;", true)]
    [InlineData("_clock\n    .Tick\n    += OnClockTick;", true)]
    [InlineData("// _timer.Tick += (_, _) => Refresh();", false)]
    [InlineData("WeakTick.Subscribe(_timer, this, static view => view.Refresh());", false)]
    public void ASubscription_IsFound_OutsideAComment(string code, bool isFound)
    {
        Assert.Equal(isFound, TickSubscription().IsMatch(WithoutLineComments(code)));
    }

    [Theory]
    [InlineData("WeakTick.Subscribe(_timer, this, static view => view.Refresh());", false)]
    [InlineData("WeakTick.Subscribe(\n    _timer,\n    this,\n    static view =>\n    {\n    });", false)]
    [InlineData("WeakTick.Subscribe(_timer, this, view => Refresh());", true)]
    [InlineData("WeakTick.Subscribe(\n    _timer, this,\n    view => Refresh());", true)]
    [InlineData("WeakTick.Subscribe(_timer, this, tick);", true)]
    public void AWeakTickWithoutAStaticLambda_IsFound(string code, bool isFound)
    {
        Assert.Equal(isFound, NonStaticWeakTick().IsMatch(WithoutLineComments(code)));
    }

    private static IEnumerable<(string File, string Code)> Sources() =>
        from file in RepositoryFiles.Under("src/Granary.App", "*.cs")
        select (RepositoryFiles.Relative(file), WithoutLineComments(File.ReadAllText(file)));

    // Each match as "file:line".
    private static IEnumerable<string> Where((string File, string Code) source, Regex pattern) =>
        from Match match in pattern.Matches(source.Code)
        select $"{source.File}:{source.Code.AsSpan(0, match.Index).Count('\n') + 1}";

    // Lines that are comments become empty, so a match spans only code and keeps its line number.
    private static string WithoutLineComments(string code) =>
        string.Join('\n', code.Split('\n').Select(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? "" : line));

    [GeneratedRegex(@"\.\s*Tick\s*\+=")]
    private static partial Regex TickSubscription();

    // The third argument of a WeakTick.Subscribe call, across lines, when it does not start with "static".
    [GeneratedRegex(@"\bWeakTick\.Subscribe\(\s*[^,()]+,\s*[^,()]+,(?>\s*)(?!static\s)")]
    private static partial Regex NonStaticWeakTick();
}
