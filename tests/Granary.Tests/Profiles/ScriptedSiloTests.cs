namespace Granary.Tests.Profiles;

/// <summary>The scripted server is read while requests arrive: tests poll for what was sent as the code under test sends it.</summary>
public sealed class ScriptedSiloTests
{
    private const string Path = "api/v2/ping";
    private const int RequestsEach = 2000;

    [Fact]
    public async Task WhatWasSent_CanBeReadWhileRequestsArrive()
    {
        var token = TestContext.Current.CancellationToken;
        var silo = new ScriptedSilo().On(HttpMethod.Get, Path, ScriptedSilo.NoContent());
        using var client = new HttpClient(silo.Handler, disposeHandler: false);

        var sending = Task.WhenAll(Task.Run(Send, token), Task.Run(Send, token));
        while (!sending.IsCompleted)
        {
            _ = silo.SentTo(HttpMethod.Get, Path);
        }
        await sending;

        Assert.Equal(2 * RequestsEach, silo.SentTo(HttpMethod.Get, Path).Count);

        async Task Send()
        {
            for (int i = 0; i < RequestsEach; i++)
            {
                using var response = await client.GetAsync(ScriptedSilo.Url(Path), token);
            }
        }
    }
}
