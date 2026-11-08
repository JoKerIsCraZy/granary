using Granary.Core.Artwork;

namespace Granary.Tests.Artwork;

/// <summary>The latest blurred stills, kept so a row scrolled back and forth does not fetch, decode and blur them again.</summary>
public sealed class BlurredStillsTests
{
    private static Uri Still(string name, string signature = "a") =>
        new($"https://silo.test/media/api/v2/artwork/episodes/{name}/w400.ab12.webp?exp=1&sig={signature}");

    private static BlurredPicture Picture() => new(40, 23, new byte[40 * 23 * 4]);

    [Fact]
    public void AStillShownAgainAboutAsWide_IsFound_AlsoUnderARenewedSignature()
    {
        var stills = new BlurredStills(capacity: 4);
        var picture = Picture();

        stills.Add(Still("1"), 320, picture);

        Assert.Same(picture, stills.Find(Still("1", signature: "b"), 324));
        Assert.Null(stills.Find(Still("1"), 640));
        Assert.Null(stills.Find(Still("2"), 320));
    }

    [Fact]
    public void PastItsCapacity_TheLeastRecentlyUsedGoes()
    {
        var stills = new BlurredStills(capacity: 2);
        stills.Add(Still("1"), 320, Picture());
        stills.Add(Still("2"), 320, Picture());
        Assert.NotNull(stills.Find(Still("1"), 320));

        stills.Add(Still("3"), 320, Picture());

        Assert.NotNull(stills.Find(Still("1"), 320));
        Assert.Null(stills.Find(Still("2"), 320));
        Assert.NotNull(stills.Find(Still("3"), 320));
    }
}
