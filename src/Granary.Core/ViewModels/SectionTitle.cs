using Granary.Core.Api.Catalog;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels;

/// <summary>
/// A Home or library row's title as shown, as the web's <c>lib/sectionTitle.ts</c>: a title Silo wrote shows in the
/// language on screen, from its <see cref="Section.TitleKey"/> filled with <see cref="Section.TitleArgs"/>; a title
/// someone typed, and every title in English, shows as the server sent it.
/// </summary>
internal static class SectionTitle
{
    public static string Shown(Section section) => AppText.ServerText(section.TitleKey, section.TitleArgs, section.Title);
}
