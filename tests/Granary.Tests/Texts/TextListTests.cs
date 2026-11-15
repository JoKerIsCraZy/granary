using Granary.Core.Texts;

namespace Granary.Tests.Texts;

/// <summary>Names in a sentence, as the web's <c>joinNames</c> lists them in English: no serial comma.</summary>
public sealed class TextListTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("Maya", "Maya")]
    [InlineData("Maya|Leo", "Maya and Leo")]
    [InlineData("Maya|Leo|Sam", "Maya, Leo and Sam")]
    [InlineData("Movies|Kids|4K Movies|Anime", "Movies, Kids, 4K Movies and Anime")]
    public void Names_AreListedWithAnd(string names, string expected)
    {
        Assert.Equal(expected, TextList.And(names.Split('|', StringSplitOptions.RemoveEmptyEntries)));
    }
}
