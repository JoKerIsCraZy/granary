using System.Globalization;
using System.Text;
using Granary.Core.ViewModels.Player;

namespace Granary.Tests.ViewModels;

/// <summary>The Discord status's two lines within what Discord takes: 2 to 128 bytes of UTF-8.</summary>
public sealed class RichPresenceTextTests
{
    private static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    private static List<string> Elements(string text)
    {
        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            elements.Add(enumerator.GetTextElement());
        }
        return elements;
    }

    [Fact]
    public void AText_StaysAsItIs_WithoutTheSpacesAroundIt() => Assert.Equal("Dark", RichPresenceText.Fit("  Dark "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoText_IsNone(string? text) => Assert.Null(RichPresenceText.Fit(text));

    [Fact]
    public void ALetterAlone_IsPadded_WithACharacterThatDoesNotShow()
    {
        string fit = RichPresenceText.Fit("M")!;

        Assert.Equal("M​", fit);
        Assert.Equal(fit, fit.Trim());
    }

    [Fact]
    public void AText_Of128Bytes_IsKeptWhole()
    {
        string text = new('a', 128);

        Assert.Equal(text, RichPresenceText.Fit(text));
    }

    [Fact]
    public void ALongerText_IsCutTo128Bytes_WithAnEllipsis()
    {
        string fit = RichPresenceText.Fit(new string('a', 200))!;

        Assert.Equal(128, Bytes(fit));
        Assert.Equal(new string('a', 125) + "…", fit);
    }

    [Fact]
    public void ACut_NeverSplitsACharacter()
    {
        string fit = RichPresenceText.Fit(string.Concat(Enumerable.Repeat("😀", 40)))!;

        Assert.InRange(Bytes(fit), 2, 128);
        Assert.Equal([.. Enumerable.Repeat("😀", Elements(fit).Count - 1), "…"], Elements(fit));
    }

    [Fact]
    public void ACut_KeepsALetterWithItsAccent()
    {
        string fit = RichPresenceText.Fit(string.Concat(Enumerable.Repeat("é", 60)))!;

        Assert.InRange(Bytes(fit), 2, 128);
        Assert.Equal([.. Enumerable.Repeat("é", Elements(fit).Count - 1), "…"], Elements(fit));
    }

    [Fact]
    public void ACut_DropsTheSpaceBeforeTheEllipsis()
    {
        string fit = RichPresenceText.Fit(new string('a', 124) + " bcdefgh")!;

        Assert.Equal(new string('a', 124) + "…", fit);
    }
}
