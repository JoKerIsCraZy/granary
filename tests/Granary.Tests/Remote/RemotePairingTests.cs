using System.Text.Json;
using Granary.Core;
using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Granary.Core.Remote;
using Granary.Tests.Fakes;

namespace Granary.Tests.Remote;

/// <summary>
/// One pairing key per PC, in the secret store only and kept until Reset Pairing Code, and the receiver's own id beside it
/// in <c>phone-remote.json</c>, which phones keep the key under.
/// </summary>
public sealed class RemotePairingTests : IDisposable
{
    private const string AnotherReceiverId = "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly InMemorySecretStore _secrets = new();
    private readonly LogSink _logs = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string IdFile => Path.Combine(_directory, "phone-remote.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private RemotePairing Create(ISecretStore? secrets = null) => new(secrets ?? _secrets, IdFile, _logs.For<RemotePairing>());

    [Fact]
    public async Task TheFirstUse_MakesAReceiverIdAndA16ByteKey()
    {
        var material = await Create().LoadOrCreateAsync(Token);

        Assert.True(Guid.TryParseExact(material.ReceiverId, "D", out _));
        Assert.Equal(16, material.Key.Length);
        Assert.Equal(SiloCastSealed.KeyId(material.Key), material.KeyId);
        Assert.Equal(Convert.ToBase64String(material.Key), _secrets.Values["phone-remote/pairing-key"]);
        Assert.Equal("phone-remote/pairing-key", RemotePairing.SecretKey);
        Assert.True(File.Exists(IdFile));
        Assert.Equal(Path.Combine(AppPaths.DataDirectory, "phone-remote.json"), RemotePairing.DefaultIdFilePath);
    }

    [Fact]
    public async Task AtTheNextStart_TheSameIdAndKeyAreBack()
    {
        var first = await Create().LoadOrCreateAsync(Token);

        var again = await Create().LoadOrCreateAsync(Token);

        Assert.Equal(first.ReceiverId, again.ReceiverId);
        Assert.Equal(first.Key.ToArray(), again.Key.ToArray());
        Assert.Equal(1, _secrets.Writes);
    }

    [Fact]
    public async Task TheKey_IsInTheSecretStoreOnly()
    {
        var material = await Create().LoadOrCreateAsync(Token);

        string file = await File.ReadAllTextAsync(IdFile, Token);
        using var document = JsonDocument.Parse(file);
        Assert.Equal(["receiver_id"], document.RootElement.EnumerateObject().Select(member => member.Name));
        Assert.Equal(material.ReceiverId, document.RootElement.GetProperty("receiver_id").GetString());
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        foreach (string text in KeyTexts(material.Key.ToArray()))
        {
            Assert.DoesNotContain(text, file, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(text, _logs.All, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task AReset_DrawsANewKey_AndKeepsTheReceiverId()
    {
        var pairing = Create();
        int changes = 0;
        pairing.KeyChanged += (_, _) => changes++;
        var before = await pairing.LoadOrCreateAsync(Token);

        var after = await pairing.ResetKeyAsync(Token);

        Assert.Equal(before.ReceiverId, after.ReceiverId);
        Assert.NotEqual(before.KeyId, after.KeyId);
        Assert.Equal(1, changes);
        Assert.Equal(Convert.ToBase64String(after.Key), _secrets.Values[RemotePairing.SecretKey]);
        Assert.Equal(after.KeyId, (await pairing.LoadOrCreateAsync(Token)).KeyId);
        Assert.Equal(after.KeyId, (await Create().LoadOrCreateAsync(Token)).KeyId);
    }

    [Fact]
    public async Task AReset_ForgetsTheOldKey()
    {
        var pairing = Create();
        var before = await pairing.LoadOrCreateAsync(Token);

        await pairing.ResetKeyAsync(Token);

        Assert.True(before.IsForgotten);
        Assert.Throws<ObjectDisposedException>(() => before.Key.Length);
        Assert.False(pairing.Current!.IsForgotten);
    }

    [Fact]
    public void AKeyInUse_StaysWholeUntilTheUseEnds_ThenIsForgotten()
    {
        var material = new PairingMaterial(AnotherReceiverId, SealedVectors.Key);

        using (var use = material.UseKey())
        {
            // Forgotten on another thread while a handshake still derives from it.
            material.Dispose();

            Assert.Equal(SealedVectors.Key, use.Key.ToArray());
            Assert.False(material.IsForgotten);
            Assert.Throws<ObjectDisposedException>(() => material.UseKey().Key.Length);
        }

        Assert.True(material.IsForgotten);
    }

    [Fact]
    public void TwoMaterials_AreNotTheSame_EvenWithTheSameKeyId()
    {
        var first = new PairingMaterial(AnotherReceiverId, SealedVectors.Key);
        var collision = new PairingMaterial(AnotherReceiverId, Convert.FromHexString("0f0e0d0c0b0a09080706050403020100"), first.KeyId);

        Assert.Equal(first.KeyId, collision.KeyId);
        Assert.True(PairingMaterial.Same(first, first));
        Assert.False(PairingMaterial.Same(first, collision));
        Assert.False(PairingMaterial.Same(first, null));
    }

    [Fact]
    public async Task AListenerThatFails_NeitherUndoesTheReset_NorKeepsItFromTheOthers()
    {
        var pairing = Create();
        int heard = 0;
        pairing.KeyChanged += (_, _) => throw new InvalidOperationException("listener failed");
        pairing.KeyChanged += (_, _) => heard++;
        var before = await pairing.LoadOrCreateAsync(Token);

        var after = await pairing.ResetKeyAsync(Token);

        Assert.NotEqual(before.KeyId, after.KeyId);
        Assert.Equal(1, heard);
        Assert.Equal(after.KeyId, (await pairing.LoadOrCreateAsync(Token)).KeyId);
        Assert.Contains("InvalidOperationException", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALostSecretStore_MeansANewKey_UnderTheOldId()
    {
        var first = await Create().LoadOrCreateAsync(Token);
        var emptied = new InMemorySecretStore();

        var again = await Create(emptied).LoadOrCreateAsync(Token);

        Assert.Equal(first.ReceiverId, again.ReceiverId);
        Assert.NotEqual(first.KeyId, again.KeyId);
        Assert.Equal(16, again.Key.Length);
        Assert.Equal(Convert.ToBase64String(again.Key), emptied.Values[RemotePairing.SecretKey]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAAA")]
    // 17 bytes.
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("not base64!")]
    public async Task AStoredKeyThatIsNot16Bytes_IsReplaced(string stored)
    {
        await Create().LoadOrCreateAsync(Token);
        _secrets.Values[RemotePairing.SecretKey] = stored;

        var material = await Create().LoadOrCreateAsync(Token);

        Assert.Equal(16, material.Key.Length);
        Assert.Equal(Convert.ToBase64String(material.Key), _secrets.Values[RemotePairing.SecretKey]);
        Assert.Contains(_logs.Lines, line => line.StartsWith("Warning ", StringComparison.Ordinal)
            && line.Contains("pairing key could not be read", StringComparison.Ordinal));
        if (stored.Length > 0)
        {
            Assert.DoesNotContain(stored, _logs.All, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{ \"receiver_id\": \"Elena")]
    [InlineData("{ \"receiver_id\": \"Elena's PC\" }")]
    [InlineData("{ \"receiver_id\": 5 }")]
    [InlineData("{ \"receiver_id\": \"\" }")]
    [InlineData("[]")]
    public async Task AnIdFileThatCannotBeRead_IsReplaced(string content)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(IdFile, content, Token);

        var material = await Create().LoadOrCreateAsync(Token);

        Assert.True(Guid.TryParseExact(material.ReceiverId, "D", out _));
        Assert.Equal(material.ReceiverId, (await Create().LoadOrCreateAsync(Token)).ReceiverId);
        Assert.Contains(_logs.Lines, line => line.StartsWith("Warning ", StringComparison.Ordinal)
            && line.Contains("could not be read", StringComparison.Ordinal));
        // Logged without its content.
        Assert.DoesNotContain("Elena", _logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain("receiver_id", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoCallersAtOnce_GetOneKey()
    {
        var reads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _secrets.ReadGate = reads.Task;
        var pairing = Create();

        var first = pairing.LoadOrCreateAsync(Token);
        var second = pairing.LoadOrCreateAsync(Token);
        reads.SetResult();
        var materials = await Task.WhenAll(first, second);

        Assert.Equal(materials[0].ReceiverId, materials[1].ReceiverId);
        Assert.Equal(materials[0].Key.ToArray(), materials[1].Key.ToArray());
        Assert.Equal(1, _secrets.Writes);
    }

    [Fact]
    public void TheMaterial_NeverPrintsItsKey()
    {
        byte[] key = SealedVectors.Key;
        var material = new PairingMaterial(AnotherReceiverId, key);

        string printed = material.ToString();

        Assert.Equal(AnotherReceiverId, printed);
        foreach (string text in KeyTexts(key))
        {
            Assert.DoesNotContain(text, printed, StringComparison.OrdinalIgnoreCase);
            Assert.All(typeof(PairingMaterial).GetProperties().Where(property => property.PropertyType == typeof(string)),
                property => Assert.DoesNotContain(text, (string)property.GetValue(material)!, StringComparison.OrdinalIgnoreCase));
        }
        // The material keeps a copy of its own: changing the bytes it was made from changes nothing.
        key[0] ^= 0xFF;
        Assert.Equal(SealedVectors.Key, material.Key.ToArray());
        Assert.Equal(SealedVectors.KeyId, material.KeyId);
    }

    [Fact]
    public async Task TheKey_OutlivesASignOutAndAServersRemoval()
    {
        var material = await Create().LoadOrCreateAsync(Token);
        var credentials = new CredentialStore(_secrets, _logs.For<CredentialStore>());
        await credentials.SetAsync("5e6f7a8b9c0d1e2f",
            new SessionCredentials("session-1", "access", "refresh", DateTimeOffset.UnixEpoch, 60, "7", null, null), Token);

        await credentials.RemoveAsync("5e6f7a8b9c0d1e2f", Token);

        Assert.Equal(material.KeyId, (await Create().LoadOrCreateAsync(Token)).KeyId);
    }

    // Every way the key could be written out: hex, base64 and the pairing link's base32.
    private static IEnumerable<string> KeyTexts(byte[] key) =>
        [Convert.ToHexString(key), Convert.ToBase64String(key), Convert.ToBase64String(key).TrimEnd('='), Base32.Encode(key)];
}
