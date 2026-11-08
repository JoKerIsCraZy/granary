using System.Net;
using Granary.Core.Auth;
using Granary.Core.Downloads;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>Whether downloads are offered: the server's capability for the profile, or downloads it has on this PC already.</summary>
public sealed class DownloadsFeatureTests : IAsyncDisposable
{
    private readonly DownloadHarness _h = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private DownloadsFeature Feature() => new(_h.Session, _h.Api, _h.Store, _h.Time, _h.Logs.For<DownloadsFeature>());

    private void Capability(bool enabled, bool allowed) =>
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Capability(enabled, allowed)));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task TheFeature_OffersDownloads_OnlyWhenEnabledAndAllowed(bool enabled, bool allowed, bool offered)
    {
        Capability(enabled, allowed);
        var feature = Feature();

        await feature.RefreshAsync(Token);

        Assert.Equal((offered, offered), (feature.CanDownload, feature.IsOffered));
        Assert.Equal("7", Assert.Single(_h.Silo.SentTo(HttpMethod.Get, CapabilityPath)).AsProfile);
    }

    [Fact]
    public async Task TheEntry_StaysWhileTheProfileHasDownloads_EvenWhenTheServerStoppedThem()
    {
        Capability(enabled: true, allowed: false);
        await _h.AddAsync("dl_1", DownloadState.Completed);
        var feature = Feature();

        await feature.RefreshAsync(Token);

        Assert.False(feature.CanDownload);
        Assert.True(feature.IsOffered);
    }

    [Fact]
    public async Task AnotherProfilesDownloads_DoNotOfferTheEntry()
    {
        Capability(enabled: false, allowed: false);
        await _h.AddAsync("dl_8", DownloadState.Completed, scope: Scope8);
        var feature = Feature();

        await feature.RefreshAsync(Token);

        Assert.False(feature.IsOffered);
    }

    [Fact]
    public async Task AFailedRead_IsTriedAgainAfterAMinute()
    {
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.NetworkError(),
            ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Capability()));
        var feature = Feature();

        await feature.RefreshAsync(Token);
        Assert.False(feature.CanDownload);
        await feature.RefreshAsync(Token);
        Assert.Single(_h.Silo.SentTo(HttpMethod.Get, CapabilityPath));

        _h.Time.Advance(DownloadsFeature.RetryAfter);
        await feature.RefreshAsync(Token);
        Assert.True(feature.CanDownload);
    }

    [Fact]
    public async Task EachProfile_IsAskedForItself_AndASignOutOffersNothing()
    {
        Capability(enabled: true, allowed: true);
        var feature = Feature();
        await feature.RefreshAsync(Token);
        await feature.RefreshAsync(Token);
        Assert.Single(_h.Silo.SentTo(HttpMethod.Get, CapabilityPath));

        _h.Session.ProfileId = "8";
        await feature.RefreshAsync(Token);
        Assert.Equal(["7", "8"], _h.Silo.SentTo(HttpMethod.Get, CapabilityPath).Select(r => r.AsProfile));

        _h.Session.State = SessionState.SignedOut;
        await feature.RefreshAsync(Token);
        Assert.Equal((false, false, (object?)null), (feature.CanDownload, feature.IsOffered, (object?)feature.Capability));
    }
}
