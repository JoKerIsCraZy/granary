using Granary.Core.Api.Profiles;
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

    // Only the server's own preset drawings may be drawn as SVG: not an upload, and not a preset from another host (DiceBear).
    [Theory]
    [InlineData("preset", "/profile-avatars/avatar-1.svg", true)]
    [InlineData("preset", "https://nas.example/silo/profile-avatars/avatar-2.svg", true)]
    [InlineData("preset", "https://api.dicebear.com/9.x/thumbs/svg?seed=Elena&size=128", false)]
    [InlineData("preset", "http://nas.example/silo/profile-avatars/avatar-1.svg", false)]
    [InlineData("preset", "https://nas.example:8443/profile-avatars/avatar-1.svg", false)]
    [InlineData("upload", "/api/v2/artwork/profile-avatars/1/7/w256.webp?exp=1&sig=a", false)]
    [InlineData(null, "/profile-avatars/avatar-1.svg", false)]
    public void OnlyTheServersOwnPresets_MayBeDrawnAsSvg(string? source, string avatarUrl, bool mayBeSvg)
    {
        var profile = new Profile("7", "Elena", true, false, false, "preset:avatar-1", source!, avatarUrl, "en", "en", "auto");

        var item = ProfileItemViewModel.From(profile, PrefixedBase);

        Assert.Equal(mayBeSvg, item.AvatarIsServerPreset);
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
        Assert.Equal("Laura (PIN protected)", locked.AccessibleName);
        Assert.Equal(locked.AccessibleName, locked.ToString());
        Assert.Equal("M", (locked with { Name = "mia" }).Initials);
    }
}
