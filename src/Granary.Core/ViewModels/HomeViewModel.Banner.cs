using System.ComponentModel;
using Granary.Core.Api.Catalog;

namespace Granary.Core.ViewModels;

// Home's banner: the first row the server marks as the hero (featured) shows at the top as slides instead of as a row, as the
// web's Home does (pages/homeSectionState.ts) and the Mac app's; without one the top of Home follows the cards.
public sealed partial class HomeViewModel
{
    // Whether the banner's row is Continue Watching or Next Up, whose titles a removal takes away.
    private bool _bannerIsWatchingRow;

    /// <summary>Home's banner; without slides the top of Home follows the cards instead (<see cref="Hero"/>).</summary>
    public FeaturedBannerViewModel Banner { get; }

    /// <summary>What the top of Home shows: the banner's slide while Home has one, else the card the pointer or the keyboard is on.</summary>
    public BrowseHeroTitle? HeroTitle => Banner.Current?.Title ?? Hero.Shown;

    // Rows or the banner to show: a refresh is then quiet, and Home is not empty.
    private bool HasContent => Rows.Count > 0 || Banner.HasSlides;

    // The first row marked as the hero, by its place: only the first counts, as on the web; a later one stays a row.
    private static int FeaturedAt(IReadOnlyList<Section> sections)
    {
        for (int i = 0; i < sections.Count; i++)
        {
            if (sections[i].Featured)
            {
                return i;
            }
        }
        return -1;
    }

    // The featured row's cards as slides, as many as the row's item limit (the web's maxSlides). A row without cards, or
    // one that could not be fetched, leaves Home without a banner.
    private void ShowBanner(Section? featured, Uri server)
    {
        _bannerIsWatchingRow = featured is not null && HomeRowRules.IsWatchingRow(featured.SectionType);
        if (featured is not { TotalCount: > 0 })
        {
            Banner.Show([]);
            return;
        }
        var items = featured.Items.Where(item => item.ContentId.Length > 0 && HomeRowRules.Shows(featured.SectionType, item));
        if (featured.ItemLimit > 0)
        {
            items = items.Take(featured.ItemLimit);
        }
        Banner.Show([.. items.Select(item => new FeaturedSlide(BrowseHeroTitle.From(item, server), item))]);
    }

    // A title removed from Continue Watching and Next Up leaves a banner made of one of them too.
    private void DropFromBanner(Func<CatalogItem, bool> sameTitle)
    {
        if (_bannerIsWatchingRow && Banner.Slides.Any(slide => sameTitle(slide.Item)))
        {
            Banner.Show([.. Banner.Slides.Where(slide => !sameTitle(slide.Item))]);
        }
    }

    private void OnBannerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FeaturedBannerViewModel.Current) or null)
        {
            OnPropertyChanged(nameof(HeroTitle));
        }
    }

    private void OnHeroChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BrowseHeroViewModel.Shown) or null)
        {
            OnPropertyChanged(nameof(HeroTitle));
        }
    }
}
