using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Granary.Core.Remote;
using Granary.Tests.Fakes;

namespace Granary.Tests.Remote;

/// <summary>
/// What the phone remote writes to the log, over everything it does with phones (Review Focus 4): pairing, a wrong key, the
/// hello, the status, commands, an offer, a launch, a reset, a change of server and the stop, every category at every
/// level. No line may hold anything of the never-list (design 6.3), and every connection's end is logged by its number
/// and a reason of the fixed list.
/// </summary>
public sealed partial class RemoteLoggingTests : IAsyncDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private readonly RemoteHarness _harness = new(lifetime: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private RemoteReceiver Receiver => _harness.Receiver;

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task NothingSecretOrPersonal_ReachesTheLog()
    {
        var neverLogged = await RunEverythingAsync();

        string log = _harness.Logs.All;
        Assert.Contains("Connection", log, StringComparison.Ordinal);
        Assert.All(neverLogged, value => Assert.DoesNotContain(value, log, StringComparison.OrdinalIgnoreCase));
        // No frame and no part of one: its text is JSON.
        Assert.DoesNotContain("{", log, StringComparison.Ordinal);
        // The profile's id where a profile is spoken of.
        Assert.DoesNotMatch(ProfileSeven(), log);
    }

    [Fact]
    public async Task EveryConnectionsEnd_IsLoggedWithItsNumberAndAReason()
    {
        await RunEverythingAsync();

        var reasons = typeof(RemoteLogReasons).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral).Select(field => (string)field.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
        var lines = _harness.Logs.Lines.ToList();
        var opened = lines.Select(line => Opened().Match(line)).Where(match => match.Success).Select(match => match.Groups[1].Value).ToList();
        var ended = lines.Select(line => Ended().Match(line)).Where(match => match.Success)
            .Select(match => (Number: match.Groups[1].Value, Reason: match.Groups[2].Value.Trim())).ToList();

        Assert.NotEmpty(opened);
        Assert.All(opened, number => Assert.Single(ended, end => end.Number == number));
        Assert.All(ended, end => Assert.Contains(end.Reason, reasons));
        // A connection that never opened ends logged all the same: the one with the wrong key.
        Assert.Contains(ended, end => end.Reason == RemoteLogReasons.WrongKey && !opened.Contains(end.Number));
        // Every number the log names has its end.
        var named = lines.SelectMany(line => Named().Matches(line)).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.All(named, number => Assert.Contains(ended, end => end.Number == number));
    }

    // The phone remote end to end; answers every value of the never-list it met on the way.
    private async Task<List<string>> RunEverythingAsync()
    {
        string otherServerId = SiloCastServerId.From(new Uri("https://other.example"));
        var neverLogged = new List<string>
        {
            "silo.example", "other.example", RemoteHarness.ServerId, otherServerId, RemoteHarness.ServerIdentity, "deploy-2",
            "Example Server", "Elena", "phone-1", "phone-2", "movie-1", "Example Movie", "resume-token-1", "request-1",
        };
        await _harness.StartListeningAsync();
        var player = new FakeRemotePlayer(_harness.Dispatcher)
        {
            Answer = command => command.Name == SiloCastControls.PlayNext ? RemoteCommandResult.Failed : RemoteCommandResult.Done,
        };
        await _harness.AttachAsync(player);
        neverLogged.Add(Receiver.PairingLink!);
        AddKey(neverLogged, RemoteHarness.KeyOf(_harness.PairingStore.Current!));

        // A phone with another key is refused at its first frame.
        var (stranger, _, strangerServing) = await _harness.ConnectThroughListenerAsync(RemoteHarness.OtherKey, pair: false);
        Assert.False(await stranger.PairAsync());
        await strangerServing.WaitAsync(HangGuard, Token);
        AddKey(neverLogged, RemoteHarness.OtherKey);

        // The phone pairs, says hello, gets the status, controls the player, offers a title and launches one.
        var (phone, _, serving) = await _harness.ConnectThroughListenerAsync();
        AddNonces(neverLogged, phone);
        await phone.NextAsync("hello");
        await phone.HelloAsync(RemoteHarness.ServerId, RemoteHarness.ServerIdentity);
        await phone.NextAsync("state");
        foreach (string control in new[]
        {
            """{"name":"seek","seconds":1798.292724609375}""", """{"name":"pause"}""", """{"name":"set_volume","volume":0.4}""",
            """{"name":"select_subtitle_track","trackId":2}""", """{"name":"play_next"}""", """{"name":"set_hdr_enabled","enabled":true}""",
        })
        {
            await phone.SendAsync($$"""{"type":"control","control":{{control}},"v":2}""");
        }
        await phone.SendAsync($$"""{"type":"handoff_offer","handoffOffer":{"requestId":"request-1","serverId":"{{RemoteHarness.ServerId}}","serverURL":"https://silo.example","profileId":"7","profileName":"Elena Moreau","resumeToken":"resume-token-1"},"v":2}""");
        await phone.SendAsync($$$"""{"type":"launch","launch":{"serverId":"{{{RemoteHarness.ServerId}}}","playback":{"contentId":"movie-1","startFromBeginning":false,"resumePosition":4747.284}},"v":2}""");
        await phone.SendAsync("""{"type":"ping","v":2}""");
        await phone.NextAsync("pong");

        // Reset Pairing Code lets the phone go; a phone on the new code says hello again.
        Assert.Equal(PairingResetOutcome.Reset, await Receiver.ResetPairingAsync());
        await serving.WaitAsync(HangGuard, Token);
        AddKey(neverLogged, RemoteHarness.KeyOf(_harness.PairingStore.Current!));
        neverLogged.Add(Receiver.PairingLink!);
        var (again, _, againServing) = await _harness.ConnectThroughListenerAsync();
        AddNonces(neverLogged, again);
        await again.NextAsync("hello");
        await again.HelloAsync(RemoteHarness.ServerId, RemoteHarness.ServerIdentity, deviceId: "phone-2");
        await again.NextAsync("state");

        // Another server: the receiver stops and starts for it; then Granary closes.
        _harness.SignIn(RemoteHarness.Server("https://other.example", "entry-2", "deploy-2"));
        await _harness.SettleUiAsync();
        await Receiver.SettledAsync().WaitAsync(HangGuard, Token);
        await againServing.WaitAsync(HangGuard, Token);
        neverLogged.Add(Receiver.PairingLink!);
        await _harness.DetachAsync(player);
        var stopping = Receiver.StopAsync();
        _harness.Time.Advance(RemoteReceiver.StopBudget);
        await stopping.WaitAsync(HangGuard, Token);
        return neverLogged;
    }

    // The key as hex, base64, base64url and base32, and its public id.
    private static void AddKey(List<string> values, byte[] key)
    {
        string base64 = Convert.ToBase64String(key);
        values.AddRange([Convert.ToHexString(key), base64, base64.TrimEnd('=').Replace('+', '-').Replace('/', '_'), Base32.Encode(key),
            FakePhone.KeyIdOf(key)]);
    }

    // Both sides' nonces of a phone's handshake, as they were sent and as hex.
    private static void AddNonces(List<string> values, FakePhone phone)
    {
        values.Add(Convert.ToBase64String(phone.Nonce).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        values.Add(Convert.ToHexString(phone.Nonce));
        using var answer = JsonDocument.Parse(phone.ReceivedSealedHello!);
        values.Add(answer.RootElement.GetProperty("nonce").GetString()!);
    }

    [GeneratedRegex(@"profile\W{0,3}7\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileSeven();

    [GeneratedRegex(@"Connection (\d+) opened")]
    private static partial Regex Opened();

    [GeneratedRegex(@"Connection (\d+) (?:closed|refused): (.+)$")]
    private static partial Regex Ended();

    [GeneratedRegex(@"Connection (\d+)\b")]
    private static partial Regex Named();
}
