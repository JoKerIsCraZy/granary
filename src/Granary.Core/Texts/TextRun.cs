namespace Granary.Core.Texts;

/// <summary>
/// A stretch of a formatted message and the rich-text tags around it, outermost first: "Narrated by <c>&lt;name&gt;</c>
/// {narrator}<c>&lt;/name&gt;</c>" is "Narrated by " with no tags, then the narrator with <c>name</c>. A page turns the
/// tags into inlines, as the web's <c>t(id, { name: chunks => … })</c> turns them into elements.
/// </summary>
public sealed record TextRun(string Text, IReadOnlyList<string> Tags);
