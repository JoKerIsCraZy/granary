using System.Xml;
using System.Xml.Linq;

namespace Granary.Tests.Browse;

/// <summary>
/// No Granary control is the root of a data template. WinUI ties each element a repeater makes from a template to the
/// template, the template to its recycle pool and the pool to the repeater. A Granary control is held by .NET, so each
/// full collection walks from it, and from a template's root that walk takes in the whole repeater: with a pool of
/// skeletons it was over a million references, and full collections took seconds. A plain Grid around the control keeps
/// the control's walk to its own children.
/// </summary>
public sealed class TemplateRootTests
{
    [Fact]
    public void NoDataTemplate_HasAGranaryControlAsItsRoot()
    {
        var found = RepositoryFiles.Under("src/Granary.App", "*.xaml")
            .SelectMany(file => GranaryRoots(XDocument.Load(file, LoadOptions.SetLineInfo))
                .Select(root => $"{RepositoryFiles.Relative(file)}:{root}"))
            .ToList();

        Assert.Empty(found);
    }

    [Fact]
    public void AGranaryControlAsATemplatesRoot_IsFound()
    {
        var page = XDocument.Parse("""
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:controls="using:Granary.App.Controls">
              <ItemsRepeater>
                <ItemsRepeater.ItemTemplate>
                  <DataTemplate>
                    <!-- A note first. -->
                    <controls:MediaRow />
                  </DataTemplate>
                </ItemsRepeater.ItemTemplate>
              </ItemsRepeater>
            </Page>
            """, LoadOptions.SetLineInfo);

        Assert.Equal(["6 MediaRow"], GranaryRoots(page));
    }

    [Fact]
    public void AGranaryControlInsideAPlainRoot_IsNotFound()
    {
        var page = XDocument.Parse("""
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:controls="using:Granary.App.Controls">
              <Page.Resources>
                <DataTemplate x:Key="Skeleton" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Grid>
                    <controls:MediaCardSkeleton />
                  </Grid>
                </DataTemplate>
              </Page.Resources>
            </Page>
            """);

        Assert.Empty(GranaryRoots(page));
    }

    // Each template whose root element is one of Granary's own types, as "line type".
    private static IEnumerable<string> GranaryRoots(XDocument document) =>
        from template in document.Descendants()
        where template.Name.LocalName == "DataTemplate"
        let root = template.Elements().FirstOrDefault(element => !element.Name.LocalName.Contains('.'))
        where root is not null && root.Name.NamespaceName.StartsWith("using:Granary.", StringComparison.Ordinal)
        select $"{((IXmlLineInfo)root).LineNumber} {root.Name.LocalName}";
}
