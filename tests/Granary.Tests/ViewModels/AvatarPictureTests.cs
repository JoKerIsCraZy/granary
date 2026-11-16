using Granary.Core.ViewModels;

namespace Granary.Tests.ViewModels;

/// <summary>
/// When a profile's avatar shows its picture and when its letter: the letter until a picture opened, the letter at once
/// without one, and a picture kept while its address, signed anew, loads again.
/// </summary>
public sealed class AvatarPictureTests
{
    private static readonly Uri Fox = new("https://silo.test/api/v2/artwork/fox.webp?exp=1&sig=a");
    private static readonly Uri FoxSignedAnew = new("https://silo.test/api/v2/artwork/fox.webp?exp=2&sig=b");
    private static readonly Uri Owl = new("https://silo.test/api/v2/artwork/owl.webp?exp=1&sig=c");

    [Fact]
    public void NoPicture_ShowsTheLetter_AndLoadsNothing()
    {
        var avatar = new AvatarPicture();

        Assert.Null(avatar.Show(null));
        Assert.False(avatar.ShowsPicture);
    }

    [Fact]
    public void APicture_ShowsTheLetterUntilItOpened()
    {
        var avatar = new AvatarPicture();

        int load = Assert.NotNull(avatar.Show(Fox));
        Assert.False(avatar.ShowsPicture);

        Assert.True(avatar.Opened(load));
        Assert.True(avatar.ShowsPicture);
    }

    [Fact]
    public void APictureThatFails_LeavesTheLetter()
    {
        var avatar = new AvatarPicture();
        int load = Assert.NotNull(avatar.Show(Fox));

        Assert.True(avatar.Failed(load));

        Assert.False(avatar.ShowsPicture);
    }

    // The server signs a picture's address anew every quarter of an hour: the picture stays while the new one loads.
    [Fact]
    public void AnAddressSignedAnew_KeepsThePictureWhileItLoads()
    {
        var avatar = new AvatarPicture();
        Assert.True(avatar.Opened(Assert.NotNull(avatar.Show(Fox))));

        int again = Assert.NotNull(avatar.Show(FoxSignedAnew));
        Assert.True(avatar.ShowsPicture);

        Assert.True(avatar.Opened(again));
        Assert.True(avatar.ShowsPicture);
    }

    // Another profile, or a sign-out, comes without a picture first: its letter shows at once, and a late load stays out.
    [Fact]
    public void NoAddress_ShowsTheLetterAtOnce_AndALateLoadStaysOut()
    {
        var avatar = new AvatarPicture();
        Assert.True(avatar.Opened(Assert.NotNull(avatar.Show(Fox))));
        int pending = Assert.NotNull(avatar.Show(FoxSignedAnew));

        Assert.Null(avatar.Show(null));
        Assert.False(avatar.ShowsPicture);

        Assert.False(avatar.Opened(pending));
        Assert.False(avatar.ShowsPicture);
    }

    [Fact]
    public void ALoadOvertakenByAnother_IsDropped()
    {
        var avatar = new AvatarPicture();
        int fox = Assert.NotNull(avatar.Show(Fox));
        int owl = Assert.NotNull(avatar.Show(Owl));

        Assert.False(avatar.Opened(fox));
        Assert.False(avatar.Failed(fox));
        Assert.False(avatar.ShowsPicture);

        Assert.True(avatar.Opened(owl));
        Assert.True(avatar.ShowsPicture);
    }

    [Fact]
    public void TheSameAddress_IsNotLoadedAgain()
    {
        var avatar = new AvatarPicture();
        Assert.True(avatar.Opened(Assert.NotNull(avatar.Show(Fox))));

        Assert.Null(avatar.Show(new Uri(Fox.AbsoluteUri)));
        Assert.True(avatar.ShowsPicture);
    }

    // A picture that fails once is asked for again when its address comes again, after another or none.
    [Fact]
    public void AFailedAddress_IsTriedAgainWhenItComesBack()
    {
        var avatar = new AvatarPicture();
        Assert.True(avatar.Failed(Assert.NotNull(avatar.Show(Fox))));
        Assert.Null(avatar.Show(null));

        Assert.NotNull(avatar.Show(Fox));
    }
}
