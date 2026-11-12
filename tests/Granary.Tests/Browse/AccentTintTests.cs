using System.Text.RegularExpressions;

namespace Granary.Tests.Browse;

/// <summary>
/// A server's accent reaches every tint of it: <c>AccentPainter</c> repaints the brushes it lists, so a tint added to
/// <c>Themes/Silo.xaml</c> and not to that list would stay the default accent's on a server with one of its own.
/// </summary>
public sealed partial class AccentTintTests
{
    [Fact]
    public void EveryTintOfTheAccent_IsRepaintedWithTheServersAccent()
    {
        string theme = File.ReadAllText(Path.Combine(RepositoryFiles.Root, "src", "Granary.App", "Themes", "Silo.xaml"));
        string painter = File.ReadAllText(Path.Combine(RepositoryFiles.Root, "src", "Granary.App", "Themes", "AccentPainter.cs"));
        var tints = TintKey().Matches(theme).Select(match => match.Groups[1].Value).Distinct().ToList();
        // The list itself, and that it is the one painted with the accent: a tint's name elsewhere in the file would not do.
        var list = PrimaryKeyList().Match(painter);

        Assert.NotEmpty(tints);
        Assert.True(list.Success, "AccentPainter.PrimaryKeys was not found");
        Assert.All(tints, tint => Assert.Contains($"\"{tint}\"", list.Groups[1].Value, StringComparison.Ordinal));
        Assert.Contains("Paint(dictionaries, PrimaryKeys, palette.Primary);", painter, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"PrimaryKeys\s*=\s*\[(.*?)\];", RegexOptions.Singleline)]
    private static partial Regex PrimaryKeyList();

    [GeneratedRegex("""x:Key="(SiloPrimaryTint\d+Brush)"\s""")]
    private static partial Regex TintKey();
}
