namespace Granary.Core.Texts;

/// <summary>
/// One part of a message. The web's catalogs hold messages compiled by formatjs (web/src/locales/&lt;tag&gt;.json), and
/// Granary's own ICU texts parse into the same parts; the type numbers named below are formatjs' <c>TYPE</c> enum.
/// </summary>
internal abstract record MessageNode;

/// <summary>Text shown as it is (type 0).</summary>
internal sealed record LiteralNode(string Value) : MessageNode;

/// <summary>A value printed as text, <c>{name}</c> (type 1).</summary>
internal sealed record ArgumentNode(string Name) : MessageNode;

/// <summary>A number printed with the language's grouping, <c>{name, number}</c> (type 2), without a style.</summary>
internal sealed record NumberNode(string Name) : MessageNode;

/// <summary>The branch a value names, else <c>other</c>: <c>{name, select, …}</c> (type 5).</summary>
internal sealed record SelectNode(string Name, IReadOnlyDictionary<string, IReadOnlyList<MessageNode>> Options) : MessageNode;

/// <summary>
/// The branch of a count's exact value (<c>=0</c>), else of its plural category less the offset:
/// <c>{name, plural, …}</c> (type 6, cardinal).
/// </summary>
internal sealed record PluralNode(string Name, IReadOnlyDictionary<string, IReadOnlyList<MessageNode>> Options, int Offset) : MessageNode;

/// <summary>The count of the plural around it, less its offset: <c>#</c> (type 7).</summary>
internal sealed record PoundNode : MessageNode
{
    public static PoundNode Instance { get; } = new();
}

/// <summary>Rich text around its children, <c>&lt;name&gt;…&lt;/name&gt;</c> (type 8).</summary>
internal sealed record TagNode(string Name, IReadOnlyList<MessageNode> Children) : MessageNode;
