using Granary.Core.Api.Catalog;
using Granary.Core.Api.Requests;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels;

/// <summary>
/// A row's title as shown, as the web's <c>lib/sectionTitle.ts</c>: a title Silo wrote shows in the language on screen, from
/// its title key filled with its title values; a title someone typed, and every title in English, shows as the server sent
/// it. Home's and a library's rows, For You's rows and Requests' Discover rows carry the key alike.
/// </summary>
internal static class SectionTitle
{
    public static string Shown(Section section) => AppText.ServerText(section.TitleKey, section.TitleArgs, section.Title);

    public static string Shown(RecommendationRow row) => AppText.ServerText(row.TitleKey, row.TitleArgs, row.Title);

    public static string Shown(DiscoverSection section) => AppText.ServerText(section.TitleKey, null, section.Title);
}
