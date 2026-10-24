using System.Text;
using Granary.Core.Security;

namespace Granary.Tests.Security;

public sealed class DpapiSecretStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "granary-tests", $"{Guid.NewGuid():N}.bin");

    [Fact]
    public async Task SetThenGet_RoundTripsAcrossInstances()
    {
        var ct = TestContext.Current.CancellationToken;
        await new DpapiSecretStore(_path).SetAsync("server/a/refresh_token", "refresh-secret-123", ct);

        string? value = await new DpapiSecretStore(_path).GetAsync("server/a/refresh_token", ct);

        Assert.Equal("refresh-secret-123", value);
    }

    [Fact]
    public async Task File_DoesNotContainThePlaintext()
    {
        var store = new DpapiSecretStore(_path);
        await store.SetAsync("server/a/access_token", "plaintext-token-value", TestContext.Current.CancellationToken);

        byte[] bytes = await File.ReadAllBytesAsync(_path, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("plaintext-token-value", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("plaintext-token-value", Encoding.Unicode.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_DeletesOnlyThatKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new DpapiSecretStore(_path);
        await store.SetAsync("a", "1", ct);
        await store.SetAsync("b", "2", ct);

        await store.RemoveAsync("a", ct);

        Assert.Null(await store.GetAsync("a", ct));
        Assert.Equal("2", await store.GetAsync("b", ct));
    }

    [Fact]
    public async Task Get_ReturnsNull_WhenNothingIsStored()
    {
        Assert.Null(await new DpapiSecretStore(_path).GetAsync("missing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnreadableFile_IsSetAsideSoSignInWorksAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllBytesAsync(_path, [1, 2, 3, 4], ct);
        var store = new DpapiSecretStore(_path);

        Assert.Null(await store.GetAsync("server/a/session", ct));
        await store.SetAsync("server/a/session", "new", ct);

        Assert.Equal("new", await new DpapiSecretStore(_path).GetAsync("server/a/session", ct));
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(_path + ".unreadable", ct));
    }

    public void Dispose()
    {
        foreach (string path in new[] { _path, _path + ".unreadable" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
