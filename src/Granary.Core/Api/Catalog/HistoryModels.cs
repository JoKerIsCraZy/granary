namespace Granary.Core.Api.Catalog;

/// <summary>The body of <c>POST /api/v2/history/remove</c>.</summary>
public sealed record HistoryRemoval(IReadOnlyList<HistoryRemovalTarget> Targets);

/// <summary>A title whose watch data goes: its history, watched state and resume position, for the acting profile.</summary>
/// <param name="Scope"><see cref="HistoryRemovalScopes.Item"/> for the title itself (a series or season with its episodes),
/// <see cref="HistoryRemovalScopes.Show"/> for a season's or episode's whole series.</param>
public sealed record HistoryRemovalTarget(string ContentId, string Scope)
{
    /// <summary>The web's target for a title of <paramref name="mediaType"/>: a series or season goes as the whole show.</summary>
    public static HistoryRemovalTarget For(string contentId, string mediaType) =>
        new(contentId, mediaType is "series" or "season" ? HistoryRemovalScopes.Show : HistoryRemovalScopes.Item);
}

public static class HistoryRemovalScopes
{
    public const string Item = "item";
    public const string Show = "show";
}
