using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Granary.Core.Settings.Search;

/// <summary>A thing a settings page shows, by the keys of its texts, and the keywords kept next to it.</summary>
public sealed record SettingsMarkupEntry(SettingsEntryKind Kind, string LabelKey, string? DescriptionKey, IReadOnlyList<string> Keywords);

/// <summary>What a settings page's markup shows that search finds: the text under its title and its settings.</summary>
public sealed record SettingsMarkupPage(string? DescriptionKey, IReadOnlyList<SettingsMarkupEntry> Entries);

/// <summary>An entry of the settings' sidebar: the page it opens, its name's key and the keywords kept next to it.</summary>
public sealed record SettingsSidebarPage(string Tag, string LabelKey, IReadOnlyList<string> Keywords);

/// <summary>
/// Reads what the settings pages show from their markup (src/Granary.App/Views), so search finds what is on screen and no
/// second list has to be kept in step with the pages. Only texts given by key (<c>{x:Bind t:AppText.Get('…')}</c>) count:
/// a text from the view model names a state, not a setting. What a template or the resources hold is left out, as it
/// shows once per item or not at all.
/// </summary>
public static partial class SettingsMarkup
{
    private const string ControlsNamespace = "using:Granary.App.Controls";
    private const string HeadingLevel = "AutomationProperties.HeadingLevel";
    private const string AutomationName = "AutomationProperties.Name";
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XName Keywords = XName.Get("SearchKeywords.Terms", ControlsNamespace);

    /// <summary>
    /// A page's settings, in the order it shows them: each section's title (SettingsGroup), each row's label
    /// (SettingsRow) with its description, each heading below the page's title, and each control named for screen readers.
    /// A text found twice counts once, as a row and the switch it names.
    /// </summary>
    public static SettingsMarkupPage ReadPage(string xaml)
    {
        var root = XDocument.Parse(xaml).Root ?? throw new FormatException("The markup has no root element.");
        string? description = null;
        var entries = new List<SettingsMarkupEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in Shown(root))
        {
            if (Attribute(element, HeadingLevel) == "Level1")
            {
                description ??= KeyOf(Attribute(element.ElementsAfterSelf().FirstOrDefault(), "Text"));
                continue;
            }
            if (EntryOf(element) is { } entry && seen.Add(entry.LabelKey))
            {
                entries.Add(entry);
            }
        }
        return new SettingsMarkupPage(description, entries);
    }

    /// <summary>The settings' sidebar: each list of pages by its name (x:Name), with its entries in order.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<SettingsSidebarPage>> ReadSidebar(string xaml)
    {
        var root = XDocument.Parse(xaml).Root ?? throw new FormatException("The markup has no root element.");
        var lists = new Dictionary<string, IReadOnlyList<SettingsSidebarPage>>(StringComparer.Ordinal);
        foreach (var list in root.Descendants().Where(element => element.Name.LocalName == "ListView"))
        {
            if ((string?)list.Attribute(XamlNamespace + "Name") is not { } name)
            {
                continue;
            }
            lists[name] =
            [
                .. from item in list.Elements()
                   where item.Name.LocalName == "ListViewItem"
                   let tag = (string?)item.Attribute("Tag")
                   let key = KeyOf(Attribute(item, AutomationName))
                   where tag is not null && key is not null
                   select new SettingsSidebarPage(tag, key, KeywordsOf(item)),
            ];
        }
        return lists;
    }

    /// <summary>The key of a text given as <c>{x:Bind t:AppText.Get('key')}</c>, else null.</summary>
    public static string? KeyOf(string? value) =>
        value is not null && TextKey().Match(value) is { Success: true } match ? match.Groups["key"].Value : null;

    private static SettingsMarkupEntry? EntryOf(XElement element)
    {
        bool isControls = element.Name.NamespaceName == ControlsNamespace;
        (SettingsEntryKind Kind, string? Label, string? Description)? found = element.Name.LocalName switch
        {
            "SettingsGroup" when isControls => (SettingsEntryKind.Section, Attribute(element, "Title"), Attribute(element, "Description")),
            "SettingsRow" when isControls => (SettingsEntryKind.Row, Attribute(element, "Label"), Attribute(element, "Description")),
            _ when Attribute(element, HeadingLevel) is { } level && level != "None" => (SettingsEntryKind.Heading, Attribute(element, "Text"), null),
            // A control's name is what a screen reader calls it; a text or a busy sign is no setting.
            not ("TextBlock" or "ProgressRing" or "ProgressBar") when Attribute(element, AutomationName) is { } name =>
                (SettingsEntryKind.Control, name, null),
            _ => null,
        };
        return found is { } entry && KeyOf(entry.Label) is { } label
            ? new SettingsMarkupEntry(entry.Kind, label, KeyOf(entry.Description), KeywordsOf(element))
            : null;
    }

    // The elements in document order, without what a template or the resources hold.
    private static IEnumerable<XElement> Shown(XElement element)
    {
        foreach (var child in element.Elements())
        {
            string name = child.Name.LocalName;
            if (name is "DataTemplate" or "ControlTemplate" or "ItemsPanelTemplate" or "Style" || name.EndsWith(".Resources", StringComparison.Ordinal))
            {
                continue;
            }
            yield return child;
            foreach (var inner in Shown(child))
            {
                yield return inner;
            }
        }
    }

    private static string? Attribute(XElement? element, string name) => (string?)element?.Attribute(name);

    private static string[] KeywordsOf(XElement element) =>
        ((string?)element.Attribute(Keywords) ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    // {x:Bind t:AppText.Get('key')}, with nothing else in the binding but its mode.
    [GeneratedRegex(@"\A\{x:Bind\s+t:AppText\.Get\(\s*'(?<key>[^']+)'\s*\)\s*(?:,\s*Mode=\w+\s*)?\}\z")]
    private static partial Regex TextKey();
}
