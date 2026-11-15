using Granary.Core.Api.Catalog;
using Granary.Core.Texts;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;

namespace Granary.Tests.ViewModels;

/// <summary>The picks of a grid that selects, as History's Select counts them.</summary>
public sealed class CardSelectionTests
{
    private static readonly Uri Server = new("https://silo.test/");

    private static MediaCard Movie(string id) =>
        MediaCardFactory.Poster(new CatalogItem { ContentId = id, Type = CatalogItemTypes.Movie, Title = "Masterplan" }, Server);

    [Theory]
    [InlineData("en", "2 selected")]
    [InlineData("de", "2 ausgewählt")]
    [InlineData("ja", "2件選択中")]
    public void TheCount_IsWordedInTheLanguage_AsTheWebsHistory(string tag, string expected)
    {
        // The web's catalog page words its History picks so (web.catalog.page.selectedCount).
        using var language = AppText.Use(TextLanguage.Load(tag));
        var selection = new CardSelection();
        selection.Start();

        selection.PickAll([Movie("movie-1"), Movie("movie-2")]);

        Assert.Equal(expected, selection.CountText);
    }
}
