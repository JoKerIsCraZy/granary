using System.Net;
using Granary.Core.Updates;
using Granary.Tests.Fakes;

namespace Granary.Tests.Updates;

/// <summary>Reading a release's small files with a size and time limit, and opening and removing a downloaded package.</summary>
public sealed class HttpReleaseFilesTests : IDisposable
{
    private static readonly Uri Url = new("https://github.com/elena/granary/releases/download/v0.2.0/SHA256SUMS");

    private readonly string _folder = Directory.CreateTempSubdirectory("granary-release-files-").FullName;
    private readonly ManualTimeProvider _clock = new();

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task AFileOfTheRelease_IsReadWhole()
    {
        var files = Create(FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "abc", "text/plain"));

        Assert.Equal("abc"u8.ToArray(), await files.GetAsync(Url, 3, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFileTheReleaseDoesNotHave_IsNull()
    {
        var files = Create(FakeHttpMessageHandler.Returning(HttpStatusCode.NotFound, "Not Found", "text/plain"));

        Assert.Null(await files.GetAsync(Url, 100, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnotherFailure_Throws()
    {
        var files = Create(FakeHttpMessageHandler.Returning(HttpStatusCode.ServiceUnavailable, "", "text/plain"));

        var problem = await Assert.ThrowsAsync<HttpRequestException>(() => files.GetAsync(Url, 100, TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, problem.StatusCode);
    }

    [Fact]
    public async Task AFileLargerThanTheLimit_IsRefused_WhetherItsLengthIsSaidOrNot()
    {
        var said = Create(FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "abcd", "text/plain"));
        var unsaid = Create(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnsizedContent("abcd"u8.ToArray()) })));

        await Assert.ThrowsAsync<InvalidDataException>(() => said.GetAsync(Url, 3, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => unsaid.GetAsync(Url, 3, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABodyThatTricklesIn_TimesOutAfterAMinute()
    {
        var body = new TricklingStream();
        var files = Create(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })));

        var reading = files.GetAsync(Url, 100, TestContext.Current.CancellationToken);
        await body.Stalled.Task.WaitAsync(TestContext.Current.CancellationToken);
        _clock.Advance(HttpReleaseFiles.Timeout - TimeSpan.FromSeconds(1));
        Assert.False(reading.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(1));

        // A real-time guard only so that a read that never ends fails the test instead of hanging it.
        await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(reading.IsCompleted, "The read is still waiting for the trickling body.");
        await Assert.ThrowsAsync<TimeoutException>(() => reading);
    }

    [Fact]
    public async Task ClosingGranary_StopsAReadAsACancellation_NotATimeout()
    {
        var body = new TricklingStream();
        var files = Create(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })));
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var reading = files.GetAsync(Url, 100, closing.Token);
        await body.Stalled.Task.WaitAsync(TestContext.Current.CancellationToken);
        await closing.CancelAsync();

        var stopped = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.IsNotType<TimeoutException>(stopped.InnerException);
    }

    [Fact]
    public async Task APackage_IsOpenedForReading_AndAMissingOneIsNull()
    {
        string path = Path.Combine(_folder, "GranaryApp-0.2.0-full.nupkg");
        await File.WriteAllTextAsync(path, "the package", TestContext.Current.CancellationToken);
        var files = Create(FakeHttpMessageHandler.Returning(HttpStatusCode.OK, ""));

        using (var stream = files.OpenRead(path))
        {
            Assert.NotNull(stream);
            Assert.Equal("the package", await new StreamReader(stream).ReadToEndAsync(TestContext.Current.CancellationToken));
        }
        Assert.Null(files.OpenRead(Path.Combine(_folder, "GranaryApp-0.3.0-full.nupkg")));
        Assert.Null(files.OpenRead(Path.Combine(_folder, "no such folder", "GranaryApp-0.3.0-full.nupkg")));
    }

    [Fact]
    public async Task APackage_IsRemoved_AndAMissingOneIsNoFailure()
    {
        string path = Path.Combine(_folder, "GranaryApp-0.2.0-full.nupkg");
        await File.WriteAllTextAsync(path, "the package", TestContext.Current.CancellationToken);
        var files = Create(FakeHttpMessageHandler.Returning(HttpStatusCode.OK, ""));

        Assert.True(files.Remove(path));
        Assert.False(File.Exists(path));
        Assert.True(files.Remove(path));
    }

    [Fact]
    public async Task APackageInUse_CannotBeRemoved()
    {
        string path = Path.Combine(_folder, "GranaryApp-0.2.0-full.nupkg");
        await File.WriteAllTextAsync(path, "the package", TestContext.Current.CancellationToken);
        var files = Create(FakeHttpMessageHandler.Returning(HttpStatusCode.OK, ""));

        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(files.Remove(path));
        }
        Assert.True(File.Exists(path));
    }

    private HttpReleaseFiles Create(HttpMessageHandler handler) => new(new OneClientFactory(handler), _clock);

    private sealed class OneClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>A body sent without a length, as a chunked response is.</summary>
    private sealed class UnsizedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>A body that sends one byte, then nothing more until the read is cancelled.</summary>
    private sealed class TricklingStream : Stream
    {
        private bool _sent;

        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                buffer.Span[0] = (byte)'a';
                return 1;
            }
            Stalled.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
