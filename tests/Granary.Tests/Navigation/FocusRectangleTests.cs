using System.Text.RegularExpressions;

namespace Granary.Tests.Navigation;

/// <summary>
/// The app moves focus without the rectangles of <c>FindNextElementOptions</c>. WinUI multiplies <c>ExclusionRect</c> and
/// <c>HintRect</c> by the display's scale and then holds them against element bounds it has divided by that scale, so
/// they are right only at 100 %. On a TV at 150 % the rectangle that kept a remote's arrows off the player's volume
/// slider lay somewhere else: focus landed on the slider, which takes every arrow for the volume, and no remote got
/// past it. An element the arrows should pass is taken out of the tab stops for the move instead.
/// </summary>
public sealed partial class FocusRectangleTests
{
    [Fact]
    public void FocusMoves_UseNoRectangle()
    {
        // Every App file, not only those that name FindNextElementOptions: a target-typed new() names no type.
        var found = Sources()
            .SelectMany(source => Where(source, FocusRectangle()))
            .ToList();

        Assert.Empty(found);
    }

    [Theory]
    [InlineData("options.ExclusionRect = bounds;", true)]
    [InlineData("new FindNextElementOptions { SearchRoot = this, HintRect = bounds }", true)]
    [InlineData("FocusManager.TryMoveFocus(direction, new() { ExclusionRect = bounds });", true)]
    [InlineData("// options.ExclusionRect = bounds;", false)]
    [InlineData("new FindNextElementOptions { SearchRoot = this }", false)]
    public void ARectangle_IsFound_OutsideAComment(string code, bool isFound)
    {
        Assert.Equal(isFound, FocusRectangle().IsMatch(WithoutLineComments(code)));
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

    [GeneratedRegex(@"\b(?:ExclusionRect|HintRect)\b")]
    private static partial Regex FocusRectangle();
}
