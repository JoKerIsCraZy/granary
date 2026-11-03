using Granary.Core.Navigation;

namespace Granary.Tests.Navigation;

/// <summary>The card Back returns to, as Silo's TV app: the same title, else the same place in the list.</summary>
public sealed class ReturnTargetTests
{
    [Fact]
    public void TheSameTitle_IsFound_WhereverItMoved()
    {
        string[] titles = ["a", "b", "c", "d"];

        Assert.Equal(3, ReturnTarget.IndexFor(titles, "d", previousIndex: 1));
    }

    [Fact]
    public void ATitleNoLongerListed_GivesItsPlace()
    {
        string[] titles = ["a", "b", "c", "d"];

        Assert.Equal(1, ReturnTarget.IndexFor(titles, "gone", previousIndex: 1));
    }

    [Fact]
    public void APlacePastTheEnd_GivesTheLastTitle()
    {
        string[] titles = ["a", "b"];

        Assert.Equal(1, ReturnTarget.IndexFor(titles, "gone", previousIndex: 7));
    }

    [Fact]
    public void AnEmptyList_GivesNothing()
    {
        Assert.Equal(-1, ReturnTarget.IndexFor(Array.Empty<string>(), "a", previousIndex: 0));
    }

    [Fact]
    public void Titles_AreMatchedByTheGivenKey()
    {
        var titles = new[] { new Title("1", "a"), new Title("2", "b") };

        Assert.Equal(1, ReturnTarget.IndexFor(titles, new Title("2", "b again"), previousIndex: 0, title => title.Id));
    }

    private sealed record Title(string Id, string Name);
}
