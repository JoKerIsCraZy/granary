using System.Globalization;
using System.Text;

namespace Granary.Core.Texts;

/// <summary>
/// Parses Granary's own texts, ICU MessageFormat source, into the parts formatjs compiles the web's catalogs into, by
/// formatjs' rules (@formatjs/icu-messageformat-parser): an apostrophe quotes only before <c>{ } &lt; &gt;</c>, or
/// <c>#</c> in a plural, and <c>''</c> is one apostrophe; <c>#</c> counts only directly in a plural; a <c>&lt;</c>
/// before a letter opens a tag. It takes what Silo allows in messages (docs/architecture/i18n.md, "Messages") and
/// <c>{name, number}</c>; number styles, dates, times and ordinals are refused, since callers pass such values as text.
/// </summary>
internal sealed class IcuMessageParser
{
    private readonly string _source;
    private int _position;

    private IcuMessageParser(string source) => _source = source;

    private enum Parent
    {
        None,
        Plural,
        Select,
    }

    private bool AtEnd => _position >= _source.Length;

    private char Current => _source[_position];

    private char Next => _position + 1 < _source.Length ? _source[_position + 1] : '\0';

    /// <exception cref="FormatException">The source is not a message Granary can show.</exception>
    public static IReadOnlyList<MessageNode> Parse(string source)
    {
        var parser = new IcuMessageParser(source);
        var message = parser.ParseMessage(nesting: 0, Parent.None, inTag: false);
        // A message at the top ends only at the end of the source.
        return parser.AtEnd ? message : throw parser.Error("unexpected text");
    }

    private List<MessageNode> ParseMessage(int nesting, Parent parent, bool inTag)
    {
        var nodes = new List<MessageNode>();
        while (!AtEnd)
        {
            char c = Current;
            if (c == '{')
            {
                nodes.Add(ParseArgument(nesting, inTag));
            }
            else if (c == '}' && nesting > 0)
            {
                break;
            }
            else if (c == '#' && parent == Parent.Plural)
            {
                _position++;
                nodes.Add(PoundNode.Instance);
            }
            else if (c == '<' && Next == '/')
            {
                if (!inTag)
                {
                    throw Error("a closing tag without its opening tag");
                }
                break;
            }
            else if (c == '<' && char.IsAsciiLetter(Next))
            {
                nodes.Add(ParseTag(nesting, parent));
            }
            else
            {
                nodes.Add(new LiteralNode(ParseLiteral(nesting, parent)));
            }
        }
        return nodes;
    }

    private string ParseLiteral(int nesting, Parent parent)
    {
        var text = new StringBuilder();
        while (!AtEnd)
        {
            char c = Current;
            if (c == '\'' && TryParseQuote(parent) is { } quoted)
            {
                text.Append(quoted);
            }
            else if (c == '<' && (char.IsAsciiLetter(Next) || Next == '/'))
            {
                break;
            }
            else if (c == '{' || (c == '#' && parent == Parent.Plural) || (c == '}' && nesting > 0))
            {
                break;
            }
            else
            {
                text.Append(c);
                _position++;
            }
        }
        return text.ToString();
    }

    // At an apostrophe: the text it quotes, or null when it is a plain apostrophe.
    private string? TryParseQuote(Parent parent)
    {
        switch (Next)
        {
            case '\'':
                _position += 2;
                return "'";
            case '{' or '}' or '<' or '>':
            case '#' when parent == Parent.Plural:
                break;
            default:
                return null;
        }
        _position++;
        var quoted = new StringBuilder().Append(Current);
        _position++;
        // Up to the closing apostrophe, which may be missing; '' inside is one apostrophe.
        while (!AtEnd)
        {
            if (Current == '\'')
            {
                if (Next != '\'')
                {
                    _position++;
                    break;
                }
                _position++;
            }
            quoted.Append(Current);
            _position++;
        }
        return quoted.ToString();
    }

    private MessageNode ParseTag(int nesting, Parent parent)
    {
        int start = _position;
        _position++;
        string name = ParseTagName();
        SkipSpace();
        if (TrySkip("/>"))
        {
            // formatjs keeps a self-closing tag as text.
            return new LiteralNode(_source[start.._position]);
        }
        if (!TrySkip(">"))
        {
            throw Error($"tag {name} is not closed");
        }
        var children = ParseMessage(nesting + 1, parent, inTag: true);
        if (!TrySkip("</"))
        {
            throw Error($"tag {name} has no closing tag");
        }
        if (AtEnd || !char.IsAsciiLetter(Current) || ParseTagName() != name)
        {
            throw Error($"tag {name} closes with another tag");
        }
        SkipSpace();
        return TrySkip(">") ? new TagNode(name, children) : throw Error($"tag {name} is not closed");
    }

    private string ParseTagName()
    {
        int start = _position;
        while (!AtEnd && (char.IsAsciiLetterOrDigit(Current) || Current is '-' or '.' or '_'))
        {
            _position++;
        }
        return _source[start.._position];
    }

    private MessageNode ParseArgument(int nesting, bool inTag)
    {
        _position++;
        SkipSpace();
        string name = ParseIdentifier();
        if (name.Length == 0)
        {
            throw Error("an argument has no name");
        }
        SkipSpace();
        if (TrySkip("}"))
        {
            return new ArgumentNode(name);
        }
        if (!TrySkip(","))
        {
            throw Error($"argument {name} is not closed");
        }
        SkipSpace();
        string type = ParseIdentifier();
        SkipSpace();
        return type switch
        {
            "number" => ParseNumber(name),
            "plural" => ParsePlural(name, nesting, inTag),
            "select" => new SelectNode(name, ParseOptions(nesting, Parent.Select, inTag)),
            _ => throw Error($"argument type '{type}' is not supported"),
        };
    }

    private NumberNode ParseNumber(string name)
    {
        if (TrySkip(","))
        {
            throw Error($"number {name} has a style, which is not supported");
        }
        ParseClose(name);
        return new NumberNode(name);
    }

    private PluralNode ParsePlural(string name, int nesting, bool inTag)
    {
        if (!TrySkip(","))
        {
            throw Error($"plural {name} has no options");
        }
        SkipSpace();
        int offset = 0;
        int start = _position;
        if (ParseIdentifier() == "offset" && TrySkip(":"))
        {
            SkipSpace();
            offset = ParseInteger();
        }
        else
        {
            _position = start;
        }
        return new PluralNode(name, ParseOptions(nesting, Parent.Plural, inTag), offset);
    }

    private Dictionary<string, IReadOnlyList<MessageNode>> ParseOptions(int nesting, Parent kind, bool inTag)
    {
        if (kind == Parent.Select && !TrySkip(","))
        {
            throw Error("a select has no options");
        }
        var options = new Dictionary<string, IReadOnlyList<MessageNode>>(StringComparer.Ordinal);
        while (true)
        {
            SkipSpace();
            int start = _position;
            string selector = ParseIdentifier();
            if (selector.Length == 0 && kind == Parent.Plural && TrySkip("="))
            {
                ParseInteger();
                selector = _source[start.._position];
            }
            if (selector.Length == 0)
            {
                break;
            }
            if (options.ContainsKey(selector))
            {
                throw Error($"option {selector} is there twice");
            }
            SkipSpace();
            if (!TrySkip("{"))
            {
                throw Error($"option {selector} has no message");
            }
            options[selector] = ParseMessage(nesting + 1, kind, inTag);
            if (!TrySkip("}"))
            {
                throw Error($"option {selector} is not closed");
            }
        }
        if (!options.ContainsKey("other"))
        {
            throw Error("options have no other");
        }
        ParseClose("options");
        return options;
    }

    private void ParseClose(string what)
    {
        SkipSpace();
        if (!TrySkip("}"))
        {
            throw Error($"{what} is not closed");
        }
    }

    private int ParseInteger()
    {
        int start = _position;
        if (!AtEnd && Current is '+' or '-')
        {
            _position++;
        }
        while (!AtEnd && char.IsAsciiDigit(Current))
        {
            _position++;
        }
        return int.TryParse(_source.AsSpan(start, _position - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw Error("a number was expected");
    }

    // Up to white space or ICU's syntax characters, as formatjs reads names and selectors.
    private string ParseIdentifier()
    {
        int start = _position;
        while (!AtEnd && !IsSpace(Current) && !IsSyntax(Current))
        {
            _position++;
        }
        return _source[start.._position];
    }

    private void SkipSpace()
    {
        while (!AtEnd && IsSpace(Current))
        {
            _position++;
        }
    }

    private bool TrySkip(string text)
    {
        if (string.CompareOrdinal(_source, _position, text, 0, text.Length) != 0)
        {
            return false;
        }
        _position += text.Length;
        return true;
    }

    // Unicode's Pattern_White_Space.
    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r' or '\u0085' or '\u200E' or '\u200F' or '\u2028' or '\u2029';

    // Unicode's Pattern_Syntax in ASCII, which is all an identifier here meets: every printable that is not a letter,
    // a digit or the underscore.
    private static bool IsSyntax(char c) => c is >= '!' and <= '~' && !char.IsAsciiLetterOrDigit(c) && c != '_';

    private FormatException Error(string problem) => new($"{problem} at {_position}");
}
