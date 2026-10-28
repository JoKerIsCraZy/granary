using System.Globalization;
using System.Text.Json;
using Granary.Core.Api.Settings;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Api.Catalog;

/// <summary>
/// The libraries a profile browses, as the web's <c>useUserLibraries</c> (<c>hooks/queries/libraries.ts</c>): the
/// server's list without the ones the profile hid (<c>ui.disabled_library_ids</c>), in its own order
/// (<c>ui.library_order</c>) where it set one. The server leaves hidden libraries out itself only for a profile with a
/// library limit. Preferences that cannot be read hide nothing, as on the web.
/// </summary>
public sealed class VisibleLibraries(CatalogApi catalog, SettingsApi settings, ILogger<VisibleLibraries> logger)
{
    public const string HiddenKey = "ui.disabled_library_ids";

    public const string OrderKey = "ui.library_order";

    /// <summary>
    /// The libraries the profile browses. Without its preferences (<see cref="VisibleLibraryList.PreferencesRead"/>
    /// false) every library is there, so a caller that keeps the list asks again.
    /// </summary>
    public async Task<VisibleLibraryList> ListAsync(Uri server, CancellationToken cancellationToken = default)
    {
        var preferences = ReadPreferencesAsync(server, cancellationToken);
        var libraries = catalog.ListLibrariesAsync(server, cancellationToken);
        await Task.WhenAll(preferences, libraries).ConfigureAwait(false);
        var read = await preferences.ConfigureAwait(false);
        var items = (await libraries.ConfigureAwait(false)).Items;
        return read is { } profile
            ? new(Arrange(items, profile.Hidden, profile.Order), PreferencesRead: true)
            : new(Arrange(items, [], []), PreferencesRead: false);
    }

    /// <summary>
    /// The libraries by the server's sort order without <paramref name="hidden"/>; those <paramref name="order"/> names
    /// first, in its order, then the rest.
    /// </summary>
    internal static IReadOnlyList<Library> Arrange(IEnumerable<Library> libraries, IReadOnlyCollection<string> hidden, IReadOnlyList<string> order)
    {
        var shown = libraries
            .Where(library => !hidden.Contains(library.Id))
            .OrderBy(library => library.SortOrder)
            .ThenBy(library => library.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (order.Count == 0)
        {
            return shown;
        }
        var position = order.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index, StringComparer.Ordinal);
        return [.. shown.Where(l => position.ContainsKey(l.Id)).OrderBy(l => position[l.Id]), .. shown.Where(l => !position.ContainsKey(l.Id))];
    }

    /// <summary>
    /// A library id list as the web reads it (<c>parseLibraryIDList</c>): the array, or the older JSON string of it;
    /// whole numbers above 0, each once. Anything else is no list.
    /// </summary>
    internal static IReadOnlyList<string> ParseIds(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.String } text)
        {
            return value is { } element ? IdsOf(element) : [];
        }
        string? json = text.GetString();
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            return IdsOf(document.RootElement);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> IdsOf(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var ids = new List<string>();
        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Number && entry.TryGetInt64(out long id) && id > 0
                && id.ToString(CultureInfo.InvariantCulture) is var key && !ids.Contains(key))
            {
                ids.Add(key);
            }
        }
        return ids;
    }

    // Null when they could not be read: a refusal, no connection, or a timeout (not the caller's cancelling).
    private async Task<(IReadOnlyCollection<string> Hidden, IReadOnlyList<string> Order)?> ReadPreferencesAsync(Uri server, CancellationToken cancellationToken)
    {
        try
        {
            var values = await settings.GetEffectiveAsync(server, [HiddenKey, OrderKey], cancellationToken).ConfigureAwait(false);
            return (ParseIds(values.Find(HiddenKey)).ToHashSet(StringComparer.Ordinal), ParseIds(values.Find(OrderKey)));
        }
        catch (Exception ex) when (ex is HttpRequestException or SiloApiException or JsonException
            || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read the profile's hidden libraries; showing them all for now");
            return null;
        }
    }
}

/// <summary>The libraries a profile browses, and whether its hidden ones and order could be read to arrange them.</summary>
public sealed record VisibleLibraryList(IReadOnlyList<Library> Items, bool PreferencesRead);
