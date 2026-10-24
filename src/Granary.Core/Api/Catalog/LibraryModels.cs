namespace Granary.Core.Api.Catalog;

/// <summary><c>GET /api/v2/user/libraries</c>.</summary>
public sealed record LibraryCollection(IReadOnlyList<Library> Items, PageInfo? Page);

/// <summary>
/// A library the profile can see (docs/api-contract.md, 7.3). Sort by <see cref="SortOrder"/> and hide the
/// ids in the profile setting <c>ui.disabled_library_ids</c>: the server leaves them out only for
/// profiles with a library restriction.
/// </summary>
/// <param name="Type">Free text: <c>movies</c>, <c>series</c>, <c>mixed</c>, …</param>
public sealed record Library(string Id, string Name, string Type, int SortOrder, string? PosterUrl);
