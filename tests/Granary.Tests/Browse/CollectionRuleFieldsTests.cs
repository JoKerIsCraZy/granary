using Granary.Core.Texts;
using Granary.Core.ViewModels.Collections;

namespace Granary.Tests.Browse;

/// <summary>The fields a smart collection's rules compare, which every keystroke in a rule looks up.</summary>
public sealed class CollectionRuleFieldsTests
{
    [Fact]
    public void TheFields_AreMadeOnce_ForALanguage()
    {
        var language = TextLanguage.Load("en");
        using var _ = AppText.Use(language);

        Assert.Same(CollectionRuleFields.All, CollectionRuleFields.All);
        Assert.Same(CollectionRuleFields.Find("watched"), CollectionRuleFields.Find("watched"));
        Assert.Same(CollectionRuleFields.Find("rating_imdb"), CollectionRuleFields.Find("rating"));
    }

    [Fact]
    public void TheFields_FollowTheLanguageOnScreen()
    {
        Assert.Equal("Watched", CollectionRuleFields.Find("watched")!.Label);

        using (AppText.Use(TextLanguage.Load("de")))
        {
            Assert.Equal("Gesehen", CollectionRuleFields.Find("watched")!.Label);
            Assert.Equal("Min.", CollectionRuleFields.Find("runtime")!.Unit);
            Assert.Contains(CollectionRuleFields.Offered("movie", extendedRules: true, showTitleRules: true), f => f.Label == "Gesehen");
        }

        Assert.Equal("Watched", CollectionRuleFields.Find("watched")!.Label);
    }

    [Fact]
    public void AFieldThisAppDoesNotKnow_IsNotFound()
    {
        Assert.Null(CollectionRuleFields.Find("mystery"));
    }
}
