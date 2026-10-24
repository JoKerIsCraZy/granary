using Granary.Core.ViewModels;

namespace Granary.Tests.Profiles;

public sealed class ProfileItemViewModelTests
{
    private static readonly Uri PrefixedBase = new("https://nas.example/silo");

    [Theory]
    [InlineData("/avatars/presets/fox.png", "https://nas.example/silo/avatars/presets/fox.png")]
    [InlineData("/api/v2/artwork/avatars/7?exp=1767225600&sig=a%2Bb", "https://nas.example/silo/api/v2/artwork/avatars/7?exp=1767225600&sig=a%2Bb")]
    [InlineData("https://s3.example/avatars/7.webp?X-Amz-Signature=abc", "https://s3.example/avatars/7.webp?X-Amz-Signature=abc")]
    public void ResolveAvatarUrl_MakesTheUrlAbsolute(string avatarUrl, string expected)
    {
        var resolved = ProfileItemViewModel.ResolveAvatarUrl(avatarUrl, PrefixedBase);

        Assert.Equal(expected, resolved?.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("//cdn.example/avatar.png")]
    [InlineData("file:///C:/Users/avatar.png")]
    [InlineData("javascript:alert(1)")]
    public void ResolveAvatarUrl_IsNull_WhenAbsentOrNotHttp(string? avatarUrl)
    {
        Assert.Null(ProfileItemViewModel.ResolveAvatarUrl(avatarUrl, PrefixedBase));
    }

    [Theory]
    [InlineData("Laura Palmer", "LP")]
    [InlineData("tom", "T")]
    [InlineData("  mary   jane watson ", "MJ")]
    [InlineData("élodie dupont", "ÉD")]
    [InlineData("🦊 Fox", "🦊F")]
    [InlineData("", "")]
    public void Initials_AreTheFirstLettersOfAtMostTwoWords(string name, string expected)
    {
        var profile = new ProfileItemViewModel("p1", name, null, false, false, false);

        Assert.Equal(expected, profile.Initials);
    }

    [Fact]
    public void AccessibleName_MentionsThePin()
    {
        var open = new ProfileItemViewModel("p1", "Laura", null, HasPin: false, IsPrimary: true, IsChild: false);
        var locked = open with { HasPin = true };

        Assert.Equal("Laura", open.AccessibleName);
        Assert.Equal("Laura, protected by a PIN", locked.AccessibleName);
        Assert.Equal(locked.AccessibleName, locked.ToString());
        Assert.Equal("M", (locked with { Name = "mia" }).Initials);
    }
}
