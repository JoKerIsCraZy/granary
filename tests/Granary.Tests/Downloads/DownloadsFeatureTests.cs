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
    public async Task ACancelledRead_IsReadAgainAtTheNextRefresh()
    {
        // The first read waits until it is cancelled (as leaving Settings does); the next one is answered.
        int reads = 0;
        _h.Silo.OnCancellable(HttpMethod.Get, CapabilityPath, async cancellationToken =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(DownloadPayloads.Capability(), System.Text.Encoding.UTF8, "application/json"),
            };
        });
        var feature = Feature();
        using var leaving = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var refresh = feature.RefreshAsync(leaving.Token);
        await Until(() => _h.Silo.SentTo(HttpMethod.Get, CapabilityPath).Count == 1, "the capability was asked for");

        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        await feature.RefreshAsync(Token);

        Assert.True(feature.CanDownload);
        Assert.Equal(2, _h.Silo.SentTo(HttpMethod.Get, CapabilityPath).Count);
    }

    [Fact]
    public async Task AnOldProfilesLateAnswer_IsDropped()
    {
        var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Silo.On(HttpMethod.Get, CapabilityPath, () => late.Task,
            ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Capability(enabled: true, allowed: false)));
        var feature = Feature();
        var forSeven = feature.RefreshAsync(Token);
        await Until(() => _h.Silo.SentTo(HttpMethod.Get, CapabilityPath).Count == 1, "profile 7's capability was asked for");

        _h.Session.ProfileId = "8";
        await feature.RefreshAsync(Token);
        late.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(DownloadPayloads.Capability(), System.Text.Encoding.UTF8, "application/json"),
        });
        await forSeven;

        Assert.Equal((false, false), (feature.CanDownload, feature.IsOffered));
        Assert.Equal(["7", "8"], _h.Silo.SentTo(HttpMethod.Get, CapabilityPath).Select(r => r.AsProfile));
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
