using Granary.Core.ViewModels.Detail;

namespace Granary.Tests.Trailers;

public sealed class TrailerFrameTests
{
    [Fact]
    public void InALargeWindow_TheFrameIsAsWideAsTheWebsDialog() => Assert.Equal((896d, 504d), TrailerFrame.Fit(1800, 1000));

    [Fact]
    public void InANarrowWindow_ItTakesTheWidth() => Assert.Equal((640d, 360d), TrailerFrame.Fit(640, 1000));

    [Fact]
    public void InALowWindow_ItTakesTheHeight() => Assert.Equal((480d, 270d), TrailerFrame.Fit(1800, 270));

    [Fact]
    public void WithoutRoom_ItIsEmpty() => Assert.Equal((0d, 0d), TrailerFrame.Fit(-20, 300));
}
