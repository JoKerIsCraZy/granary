using System.ComponentModel;
using Granary.Core.Api.Catalog;
using Granary.Core.Settings;
using Granary.Core.ViewModels.Detail;

namespace Granary.Core.ViewModels;

// The profile's Title Pages settings on this page (Settings → Title Pages, the web's TitlePagesSettings.tsx): the advisory
// age in the meta line. An open page follows a change.
public sealed partial class ItemDetailViewModel
{
    // The counts the sections brought for the meta line, kept so the line can be written again when a setting changes.
    private IReadOnlyList<Season>? _metaSeasons;
    private int? _metaListedEpisodes;

    private TitlePagePrefs TitlePage => _titlePage?.Shown ?? TitlePagePrefs.Unknown;

    private void ResetTitlePage()
    {
        _metaSeasons = null;
        _metaListedEpisodes = null;
        MetaLine = "";
    }

    private void FollowTitlePage()
    {
        _titlePage?.PropertyChanged += OnTitlePageChanged;
        ShowTitlePage();
    }

    private void LeaveTitlePage() => _titlePage?.PropertyChanged -= OnTitlePageChanged;

    private void OnTitlePageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TitlePageSettings.Shown))
        {
            ShowTitlePage();
        }
    }

    private void ShowTitlePage() => ShowMetaLine();

    // The advisory age follows the rating while the profile asked for it, as the web's MetadataBadges shows it.
    private void ShowMetaLine(IReadOnlyList<Season>? seasons = null, int? listedEpisodes = null)
    {
        _metaSeasons = seasons ?? _metaSeasons;
        _metaListedEpisodes = listedEpisodes ?? _metaListedEpisodes;
        if (_item is { } item)
        {
            MetaLine = DetailFacts.MetaLine(item, _metaSeasons, _metaListedEpisodes, TitlePage.ShowsAdvisoryAge);
        }
    }
}
