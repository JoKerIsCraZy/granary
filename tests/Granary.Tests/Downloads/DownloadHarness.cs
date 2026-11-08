using System.Net;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Downloads;
using Granary.Core.Auth;
using Granary.Core.Downloads;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.Downloads;

/// <summary>
/// Downloads on a scripted server: a store in a temporary folder, a session ready on <see cref="ScriptedSilo.Server"/> with
/// account <c>1</c> and profile <c>7</c>, a manual clock, a network link, free space and this PC's settings as the test sets
/// them, and the queue over all of it.
/// </summary>
internal sealed class DownloadHarness : IAsyncDisposable
{
    public static readonly DownloadScope Scope7 = new("srv1", "1", "7");
    public static readonly DownloadScope Scope8 = new("srv1", "1", "8");

    private readonly List<DownloadQueue> _queues = [];

    public DownloadHarness()
    {
        Preferences = new InMemoryDownloadPreferences(Root);
        Session = new FakeSessionManager
        {
            State = SessionState.Ready,
            ActiveServer = ScriptedSilo.Server,
            Account = new Account("1", "elena", "", "user", [], true, false, null),
            ProfileId = "7",
        };
        Hop = new FakeHttpMessageHandler((_, _) => throw new InvalidOperationException("No hop was expected"));
        Api = new DownloadsApi(Silo.Http, new FakeHttpClients(Hop), Time);
        Store = NewStore();
    }

    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));

    public string Root => Path.Combine(Directory, "downloads");

    public string IndexPath => Path.Combine(Directory, "data", "downloads.json");

    public ScriptedSilo Silo { get; } = new();

    public ManualTimeProvider Time { get; } = new();

    public LogSink Logs { get; } = new();

    public FakeSessionManager Session { get; }

    public FakeNetworkLink Network { get; } = new();

    public FakeDiskSpace Disk { get; } = new();

    public InMemoryDownloadPreferences Preferences { get; }

    public FakeHttpMessageHandler Hop { get; }

    public DownloadsApi Api { get; }

    public DownloadStore Store { get; private set; }

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A queue over this harness, not started yet; it is stopped when the harness goes.</summary>
    public DownloadQueue NewQueue()
    {
        var transfer = new DownloadTransfer(Api, Disk, Time, Logs.For<DownloadTransfer>());
        var queue = new DownloadQueue(Store, transfer, Api, Session, Network, Preferences, Time, Logs.For<DownloadQueue>());
        _queues.Add(queue);
        return queue;
    }

    /// <summary>A started queue.</summary>
    public DownloadQueue StartQueue()
    {
        var queue = NewQueue();
        queue.Start();
        return queue;
    }

    /// <summary>As at Granary's next start: the queues stop, and a new store reads the index.</summary>
    public async Task RestartAsync()
    {
        foreach (var queue in _queues)
        {
            await queue.StopAsync();
            queue.Dispose();
        }
        _queues.Clear();
        Store = NewStore();
        await Store.LoadAsync(Token);
    }

    /// <summary>Keeps a download of <paramref name="scope"/> (profile 7's by default), created a minute after the one before.</summary>
    public async Task<StoredDownload> AddAsync(string id, DownloadState state = DownloadState.Queued, long size = 3000,
        DownloadScope? scope = null, string? validator = null, string mediaItemId = "mv_1")
    {
        await Store.LoadAsync(Token);
        var record = new DownloadRecord
        {
            DownloadId = id,
            Scope = scope ?? Scope7,
            MediaItemId = mediaItemId,
            Title = "Example Movie",
            Quality = DownloadQualities.Original,
            Revision = 1,
            ExpectedBytes = size,
            State = state,
            Validator = validator,
            CreatedAt = ManualTimeProvider.DefaultStart.AddMinutes(Store.All.Count),
        };
        return await Store.AddAsync(record, Token);
    }

    public StoredDownload? Find(string id, DownloadScope? scope = null) => Store.Find(scope ?? Scope7, id);

    /// <summary>The file route of <paramref name="id"/>: each request takes the next answer, the last one repeating.</summary>
    public void ServeFile(string id, params Func<Task<HttpResponseMessage>>[] answers)
    {
        Silo.On(HttpMethod.Get, FilePath(id), answers);
        AcceptReports(id);
    }

    /// <summary>The whole file at once, with ETag <paramref name="etag"/>.</summary>
    public void ServeFile(string id, byte[] body, string etag = "\"v1\"") =>
        ServeFile(id, DownloadPayloads.Bytes(HttpStatusCode.OK, body, etag));

    /// <summary>A file whose bytes arrive as the test releases them.</summary>
    public GatedMediaStream ServeGated(string id, byte[] body, string etag = "\"v1\"", params Func<Task<HttpResponseMessage>>[] then)
    {
        var stream = new GatedMediaStream(body);
        ServeFile(id, [DownloadPayloads.Streamed(HttpStatusCode.OK, stream, body.Length, etag), .. then]);
        return stream;
    }

    /// <summary>Status reports for <paramref name="id"/> are taken (200 with the entry).</summary>
    public void AcceptReports(string id, params Func<Task<HttpResponseMessage>>[] answers) =>
        Silo.On(HttpMethod.Patch, EntryPath(id), answers.Length > 0 ? answers : [ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Entry(id, "downloading"))]);

    public IReadOnlyList<SentRequest> FileRequests(string id) => Silo.SentTo(HttpMethod.Get, FilePath(id));

    public IReadOnlyList<SentRequest> Reports(string id) => Silo.SentTo(HttpMethod.Patch, EntryPath(id));

    public static string FilePath(string id) => $"api/v2/downloads/{id}/file";

    public static string EntryPath(string id) => $"api/v2/downloads/{id}";

    /// <summary>The session acts as <paramref name="profileId"/> now, and says so.</summary>
    public void SwitchProfile(string profileId)
    {
        Session.ProfileId = profileId;
        Session.RaiseChanged();
    }

    public void SignOut()
    {
        Session.State = SessionState.SignedOut;
        Session.ProfileId = null;
        Session.RaiseChanged();
    }

    /// <summary>Moves the clock and lets what its timers started reach the scripted server.</summary>
    public async Task AdvanceAsync(TimeSpan by, DownloadQueue queue)
    {
        Time.Advance(by);
        await queue.Idle.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    /// <summary>Waits until <paramref name="condition"/> holds: the queue's work runs on its own threads.</summary>
    public static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting until " + what);
            }
            await Task.Delay(5, Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var queue in _queues)
        {
            await queue.StopAsync();
            queue.Dispose();
        }
        await Store.FolderWork;
        for (int tries = 1; System.IO.Directory.Exists(Directory); tries++)
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException) when (tries < 20)
            {
                // A part a cancelled transfer closes a moment later.
                await Task.Delay(25);
            }
        }
    }

    private DownloadStore NewStore() => new(IndexPath, Preferences, Time, Logs.For<DownloadStore>());
}

/// <summary>A media body whose bytes arrive only as the test releases them; it can also break off, as a lost connection does.</summary>
internal sealed class GatedMediaStream(byte[] bytes) : Stream
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _allowed;
    private long _position;
    private bool _broken;

    /// <summary>How many bytes were read so far.</summary>
    public long Delivered
    {
        get
        {
            lock (_gate)
            {
                return _position;
            }
        }
    }

    /// <summary>Lets <paramref name="count"/> more bytes through.</summary>
    public void Release(long count) => Change(() => _allowed = Math.Min(bytes.Length, _allowed + count));

    public void ReleaseAll() => Release(bytes.Length);

    /// <summary>The next read that waits fails, as a connection that broke off.</summary>
    public void Break() => Change(() => _broken = true);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_position == bytes.Length)
                {
                    return 0;
                }
                if (_position < _allowed)
                {
                    int count = (int)Math.Min(buffer.Length, _allowed - _position);
                    bytes.AsMemory((int)_position, count).CopyTo(buffer);
                    _position += count;
                    return count;
                }
                if (_broken)
                {
                    throw new IOException("The connection broke off.");
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Change(Action change)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            change();
            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
    }
}
