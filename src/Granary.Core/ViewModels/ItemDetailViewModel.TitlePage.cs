using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api.Catalog;
using Granary.Core.Settings;
using Granary.Core.ViewModels.Detail;

namespace Granary.Core.ViewModels;

// The profile's Title Pages settings on this page (Settings → Title Pages, the web's TitlePagesSettings.tsx): the title
// named with its logo or in text, and the advisory age in the meta line. An open page follows a change.
public sealed partial class ItemDetailViewModel
{
    // The counts the sections brought for the meta line, kept so the line can be written again when a setting changes.
    private IReadOnlyList<Season>? _metaSeasons;
    private int? _metaListedEpisodes;

    // The title's logo, which LogoUrl shows while title art is on.
    private Uri? _titleLogo;

    /// <summary>
    /// The title has a logo and the profile's choice of title art is not known yet: neither the logo nor the text shows, as
    /// the web's DetailHero holds the title rather than flash the wrong one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsTitleText))]
    public partial bool HoldsTitle { get; private set; }

    // A page made without the settings keeps the contract's defaults, the logo among them.
    private TitlePagePrefs TitlePage => _titlePage?.Shown ?? TitlePagePrefs.Defaults;

    private void ResetTitlePage()
    {
        _metaSeasons = null;
        _metaListedEpisodes = null;
        _titleLogo = null;
        HoldsTitle = false;
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

    private void ShowTitlePage()
    {
        ShowTitleArt();
        ShowMetaLine();
    }

    // As the web's DetailHero: the logo while title art is on, the name in text while it is off, neither until it is known.
    private void ShowTitleArt()
    {
        bool? titleArt = TitlePage.ShowsTitleArt;
        LogoUrl = titleArt == true ? _titleLogo : null;
        HoldsTitle = titleArt is null && _titleLogo is not null;
    }

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
