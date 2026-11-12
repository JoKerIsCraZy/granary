using System.Text.RegularExpressions;
using Granary.Core.Texts;

namespace Granary.Tests.Texts;

/// <summary>
/// Every key Granary's code names for a text exists in the English catalog, the web's or Granary's own: English is every
/// message's fallback, so a key missing there shows as a bracketed key. The web's typecheck does the same with its
/// generated <c>MessageKey</c> union (web/src/i18n/README.md, "The API"); here the sources are read, as
/// <c>TimerTickTests</c> reads them.
/// </summary>
public sealed partial class TextKeysTests
{
    [Fact]
    public void EveryKeyInTheCode_IsInTheEnglishCatalog()
    {
        var english = TextCatalog.Embedded(AppLanguage.English);

        var missing = Uses()
            .Where(use => !english.Contains(use.Key))
            .Select(use => $"{use.Where}: {use.Key}")
            .ToList();

        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("AppText.Get(\"common.cancel\")", "common.cancel")]
    [InlineData("AppText.Get(\"web.home.title\", \"count\", 3)", "web.home.title")]
    [InlineData("AppText.Runs(\n    \"granary.update.notes\", (\"version\", v))", "granary.update.notes")]
    [InlineData("Text=\"{x:Bind t:AppText.Get('common.save')}\"", "common.save")]
    [InlineData("Content=\"{x:Bind t:AppText.Get( 'common.back' ), Mode=OneTime}\"", "common.back")]
    public void AKeyGivenAsALiteral_IsFound(string code, string key)
    {
        Assert.Equal([key], Keys(code));
    }

    [Theory]
    [InlineData("AppText.Get(key)")]
    [InlineData("AppText.Get(section.TitleKey, \"genres\", genres)")]
    [InlineData("// AppText.Get(\"common.cancel\")")]
    [InlineData("<!-- {x:Bind t:AppText.Get('common.save')} -->")]
    [InlineData("/// <c>AppText.Get(\"common.cancel\")</c>")]
    public void AKeyThatIsNoLiteral_OrIsInAComment_IsNotChecked(string code)
    {
        Assert.Empty(Keys(code));
    }

    private static IEnumerable<(string Where, string Key)> Uses() =>
        from file in RepositoryFiles.Under("src", "*.cs").Concat(RepositoryFiles.Under("src", "*.xaml"))
        let code = File.ReadAllText(file)
        from Match match in KeyLiteral().Matches(WithoutComments(code))
        select ($"{RepositoryFiles.Relative(file)}:{code.AsSpan(0, match.Index).Count('\n') + 1}", Key(match));

    private static string[] Keys(string code) => [.. KeyLiteral().Matches(WithoutComments(code)).Select(Key)];

    private static string Key(Match match) => match.Groups["cs"].Success ? match.Groups["cs"].Value : match.Groups["xaml"].Value;

    // Comment lines and XAML comments become blank, so a match spans only code and keeps its line number.
    internal static string WithoutComments(string code)
    {
        string withoutLines = string.Join('\n', code.Split('\n')
            .Select(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? "" : line));
        return XamlComment().Replace(withoutLines, comment => new string('\n', comment.Value.Count('\n')));
    }

    // The first argument of AppText.Get or AppText.Runs when it is a string literal: "…" in C#, '…' in an x:Bind.
    [GeneratedRegex("""\bAppText\s*\.\s*(?:Get|Runs)\s*\(\s*(?:"(?<cs>[^"\\]+)"|'(?<xaml>[^'^]+)')""")]
    private static partial Regex KeyLiteral();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XamlComment();
}
