using System.Text;
using System.Text.Json;
using Granary.Core.Texts;

namespace Granary.Tests.Texts;

/// <summary>
/// Granary's own texts are ICU MessageFormat source, parsed into the nodes formatjs compiles the web's catalogs into. Each
/// source here must give exactly the AST upstream's en.json holds for that message (silo-server 0e95e55b6), and the
/// parser follows formatjs' rules for apostrophes, <c>#</c> and tags (@formatjs/icu-messageformat-parser).
/// </summary>
public sealed class IcuMessageParserTests
{
    [Theory]
    [InlineData("Cancel",
        """[{"type": 0, "value": "Cancel"}]""")]
    [InlineData("Because you enjoy {genres}",
        """[{"type": 0, "value": "Because you enjoy "}, {"type": 1, "value": "genres"}]""")]
    [InlineData("{seconds, plural, one {# second} other {# seconds}}",
        """[{"type": 6, "value": "seconds", "options": {"one": {"value": [{"type": 7}, {"type": 0, "value": " second"}]}, "other": {"value": [{"type": 7}, {"type": 0, "value": " seconds"}]}}, "offset": 0, "pluralType": "cardinal"}]""")]
    [InlineData("{count, plural, =0 {Not in a collection yet} one {In {count_text} collection} other {In {count_text} collections}}",
        """[{"type": 6, "value": "count", "options": {"=0": {"value": [{"type": 0, "value": "Not in a collection yet"}]}, "one": {"value": [{"type": 0, "value": "In "}, {"type": 1, "value": "count_text"}, {"type": 0, "value": " collection"}]}, "other": {"value": [{"type": 0, "value": "In "}, {"type": 1, "value": "count_text"}, {"type": 0, "value": " collections"}]}}, "offset": 0, "pluralType": "cardinal"}]""")]
    [InlineData("{kind, select, movie {Movies} other {TV}}",
        """[{"type": 5, "value": "kind", "options": {"movie": {"value": [{"type": 0, "value": "Movies"}]}, "other": {"value": [{"type": 0, "value": "TV"}]}}}]""")]
    [InlineData("Narrated by <name>{narrator}</name>",
        """[{"type": 0, "value": "Narrated by "}, {"type": 8, "value": "name", "children": [{"type": 1, "value": "narrator"}]}]""")]
    [InlineData("<b>{count}</b> new",
        """[{"type": 8, "value": "b", "children": [{"type": 1, "value": "count"}]}, {"type": 0, "value": " new"}]""")]
    [InlineData("{day, select, 1 {1st} 2 {2nd} 3 {3rd} 21 {21st} 22 {22nd} 23 {23rd} 31 {31st} other {{day}th}}",
        """[{"type": 5, "value": "day", "options": {"1": {"value": [{"type": 0, "value": "1st"}]}, "2": {"value": [{"type": 0, "value": "2nd"}]}, "3": {"value": [{"type": 0, "value": "3rd"}]}, "21": {"value": [{"type": 0, "value": "21st"}]}, "22": {"value": [{"type": 0, "value": "22nd"}]}, "23": {"value": [{"type": 0, "value": "23rd"}]}, "31": {"value": [{"type": 0, "value": "31st"}]}, "other": {"value": [{"type": 1, "value": "day"}, {"type": 0, "value": "th"}]}}}]""")]
    [InlineData("{day, number}",
        """[{"type": 2, "value": "day", "style": null}]""")]
    public void ASource_ParsesToTheAstFormatjsCompiles(string source, string ast)
    {
        Assert.Equal(Describe(FromAst(ast)), Describe(IcuMessageParser.Parse(source)));
    }

    [Theory]
    [InlineData("Couldn't load", "Couldn't load")]
    [InlineData("It''s here", "It's here")]
    [InlineData("'{name}' stays", "{name} stays")]
    [InlineData("a '}' b", "a } b")]
    [InlineData("'<b>' is no tag", "<b> is no tag")]
    [InlineData("Unclosed '{quote", "Unclosed {quote")]
    [InlineData("A } alone", "A } alone")]
    [InlineData("5 < 6 and # too", "5 < 6 and # too")]
    public void Apostrophes_AndLoneSyntax_AreLiteral(string source, string text)
    {
        Assert.Equal($"lit({text})", Describe(IcuMessageParser.Parse(source)));
    }

    [Fact]
    public void AQuotedPound_InAPlural_IsLiteral()
    {
        var nodes = IcuMessageParser.Parse("{n, plural, other {'#' # Nr.}}");

        Assert.Equal("plural(n,0,other:[lit(# ), pound, lit( Nr.)])", Describe(nodes));
    }

    [Fact]
    public void APound_InASelectInsideAPlural_IsLiteral()
    {
        var nodes = IcuMessageParser.Parse("{n, plural, other {{kind, select, other {# left}}}}");

        Assert.Equal("plural(n,0,other:[select(kind,other:[lit(# left)])])", Describe(nodes));
    }

    [Fact]
    public void AnOffset_IsRead()
    {
        var nodes = IcuMessageParser.Parse("{count, plural, offset:1 =0 {Nobody} other {# more}}");

        Assert.Equal("plural(count,1,=0:[lit(Nobody)],other:[pound, lit( more)])", Describe(nodes));
    }

    [Fact]
    public void ASelfClosingTag_IsLiteral()
    {
        Assert.Equal("lit(a), lit(<br/>), lit(b)", Describe(IcuMessageParser.Parse("a<br/>b")));
    }

    [Fact]
    public void WhitespaceInsideAnArgument_IsIgnored()
    {
        Assert.Equal("arg(name)", Describe(IcuMessageParser.Parse("{ name }")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{name")]
    [InlineData("{name, plural, one {x}}")]
    [InlineData("{name, select, other {x} other {y}}")]
    [InlineData("{name, selectordinal, other {#th}}")]
    [InlineData("{name, date, short}")]
    [InlineData("{name, number, percent}")]
    [InlineData("{name, nonsense}")]
    [InlineData("<b>open")]
    [InlineData("<b>x</i>")]
    [InlineData("x</b>")]
    [InlineData("{a, plural, other {x}")]
    public void AMalformedOrUnsupportedSource_IsRefused(string source)
    {
        Assert.Throws<FormatException>(() => IcuMessageParser.Parse(source));
    }

    private static IReadOnlyList<MessageNode> FromAst(string ast)
    {
        using var document = JsonDocument.Parse(ast);
        return AstReader.ReadMessage(document.RootElement);
    }

    // A node tree as one line, its options sorted, so two trees compare as text.
    internal static string Describe(IReadOnlyList<MessageNode> nodes) => string.Join(", ", nodes.Select(Describe));

    private static string Describe(MessageNode node) => node switch
    {
        LiteralNode literal => $"lit({literal.Value})",
        ArgumentNode argument => $"arg({argument.Name})",
        NumberNode number => $"number({number.Name})",
        PoundNode => "pound",
        SelectNode select => $"select({select.Name},{Options(select.Options)})",
        PluralNode plural => $"plural({plural.Name},{plural.Offset},{Options(plural.Options)})",
        TagNode tag => $"tag({tag.Name},[{Describe(tag.Children)}])",
        _ => throw new ArgumentOutOfRangeException(nameof(node)),
    };

    private static string Options(IReadOnlyDictionary<string, IReadOnlyList<MessageNode>> options)
    {
        var text = new StringBuilder();
        foreach (var (selector, message) in options.OrderBy(option => option.Key, StringComparer.Ordinal))
        {
            text.Append(text.Length == 0 ? "" : ",").Append(selector).Append(":[").Append(Describe(message)).Append(']');
        }
        return text.ToString();
    }
}
