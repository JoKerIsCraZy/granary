using Granary.Core.Downloads;
using Granary.Tests.Fakes;

namespace Granary.Tests.Downloads;

/// <summary>
/// What a sign-out took out of a file, noted beside it until the file is written without it: the note by itself, as the
/// file's owner uses it. What it does for the positions still to send and the kept playback settings is in their tests.
/// </summary>
public sealed class RemovalNoteTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();

    // How often the note asked its file's owner to write once more.
    private int _writesAskedFor;

    public RemovalNoteTests() => Directory.CreateDirectory(_folder);

    private string NotePath => Path.Combine(_folder, "entries.json.removed");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // A start of Granary: the note as the disk has it.
    private RemovalNote Open() => new(Path.Combine(_folder, "entries.json"), "the entries kept on this PC", _logs.For<RemovalNote>(), _time,
        () => _writesAskedFor++);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // A note that could not be understood takes everything, once: the file written after it names it. When that note could
    // not be deleted and a later removal is written into it, it is still the note the file names.
    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "version": 2, "removals": [ { "id": "a1", "server_entry_id": "srv2" } ] }""")]
    public async Task ANoteThatCouldNotBeUnderstood_IsStillTheOneItsFileNames_AfterALaterRemovalWasWrittenIntoIt(string content)
    {
        await File.WriteAllTextAsync(NotePath, content, Token);
        var note = Open();
        Assert.True(note.TryRead());
        Assert.True(note.Covers("srv1", "1", null));
        // The file is written without everything it held and names the note, which stays where it is.
        var named = note.Ids();

        // A later removal is written into the same note, and Granary closes before the file is written again.
        note.Add("srv2", null);
        note.Save();
        var next = Open();
        Assert.True(next.TryRead());

        // Only the later removal takes anything out of the file: what was recorded since the first note stays.
        Assert.False(next.Covers("srv1", "1", named));
        Assert.True(next.Covers("srv2", "1", named));
        Assert.Equal(note.Ids(), next.Ids());
        // Said once, by the start that could not understand it.
        Assert.Single(_logs.Lines);
    }

    // The owner's write put neither the note nor its file on the disk: only this run knows of the removal, and nothing
    // else may change the file before Granary closes. So the note asks for the write again by itself.
    [Fact]
    public void ARemovalThatIsOnNoDisk_AsksForItsFilesWriteAgain_LaterEachTime_UntilItIsNoted()
    {
        // A folder where the note should be: neither read nor written.
        Directory.CreateDirectory(NotePath);
        var note = Open();
        note.Add("srv1", "1");

        note.Save();
        note.NotWritten();
        Assert.Equal(RemovalNote.FirstRetry, _time.NextDue);
        _time.Advance(RemovalNote.FirstRetry);
        Assert.Equal(1, _writesAskedFor);

        // The write it asked for fared no better: the next one waits twice as long.
        note.Save();
        note.NotWritten();
        Assert.Equal(RemovalNote.FirstRetry * 2, _time.NextDue);
        Directory.Delete(NotePath);
        _time.Advance(RemovalNote.FirstRetry * 2);
        Assert.Equal(2, _writesAskedFor);

        // The note is on the disk now, though the file still is not: the next start finds it, and nothing is asked again.
        note.Save();
        note.NotWritten();
        Assert.True(File.Exists(NotePath));
        Assert.Null(_time.NextDue);
    }

    // Two writes can fail one after the other before the first try comes: a sign-out's, and the one of a change behind it.
    [Fact]
    public void AWriteThatFailsWhileATryWaits_SetsThatTryAnew_AndOnlyTheOneComes()
    {
        Directory.CreateDirectory(NotePath);
        var note = Open();
        note.Add("srv1", "1");
        note.Save();
        note.NotWritten();
        _time.Advance(TimeSpan.FromSeconds(2));

        // The next write fares no better and says so too: the try waits its whole time from now.
        note.Save();
        note.NotWritten();
        Assert.Equal(RemovalNote.FirstRetry, _time.NextDue);

        // Nothing comes when the first one was due, and one write is asked for when the second is.
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(0, _writesAskedFor);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, _writesAskedFor);
        Assert.Null(_time.NextDue);
    }

    // The system's timer may have its callback on the way to the thread pool already when a write that failed meanwhile
    // sets the try anew: the callback then comes late, for a try that no longer waits.
    [Fact]
    public void ACallbackOfATryThatWasSetAnewMeanwhile_AsksForNothing_AndLeavesTheNewTryAsItIs()
    {
        Directory.CreateDirectory(NotePath);
        var timers = new CallbacksKept(_time);
        var note = new RemovalNote(Path.Combine(_folder, "entries.json"), "the entries kept on this PC", _logs.For<RemovalNote>(), timers,
            () => _writesAskedFor++);
        note.Add("srv1", "1");
        note.Save();
        note.NotWritten();
        var late = Assert.Single(timers.Callbacks);
        note.Save();
        note.NotWritten();

        late();

        // No write before its time, and the new try still waits as long as it did.
        Assert.Equal(0, _writesAskedFor);
        Assert.Equal(RemovalNote.FirstRetry, _time.NextDue);
        _time.Advance(RemovalNote.FirstRetry);
        Assert.Equal(1, _writesAskedFor);
        // Nor did the late one count as a try: the wait after the first is twice as long, not four times.
        note.Save();
        note.NotWritten();
        Assert.Equal(RemovalNote.FirstRetry * 2, _time.NextDue);
    }

    [Fact]
    public void TheWaitBetweenTwoTries_GrowsToFiveMinutesAtMost()
    {
        Directory.CreateDirectory(NotePath);
        var note = Open();
        note.Add("srv1", "1");

        for (int tries = 0; tries < 12; tries++)
        {
            note.Save();
            note.NotWritten();
            _time.Advance(_time.NextDue ?? TimeSpan.Zero);
        }
        note.Save();
        note.NotWritten();

        Assert.Equal(12, _writesAskedFor);
        Assert.Equal(TimeSpan.FromMinutes(5), _time.NextDue);
    }

    [Fact]
    public async Task OnceTheFileIsOnTheDiskWithoutTheRemoval_NothingIsTriedAgain()
    {
        // A note of an earlier sign-out, done with, that something still has open: a new one cannot take its place.
        await File.WriteAllTextAsync(NotePath, """{ "version": 1, "removals": [] }""", Token);
        var note = Open();
        using (new FileStream(NotePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(note.TryRead());
            note.Add("srv1", "1");
            note.Save();
            note.NotWritten();
            Assert.Equal(RemovalNote.FirstRetry, _time.NextDue);

            // A change gets the owner's file written before the try: the removal is out of it, though the note is still held.
            note.Done(note.Ids());
        }

        Assert.Null(_time.NextDue);
        _time.Advance(RemovalNote.MaxRetry);
        Assert.Equal(0, _writesAskedFor);
    }

    [Fact]
    public void ARemovalThatIsNoted_AsksForNothing_WhenItsFileCouldNotBeWritten()
    {
        var note = Open();
        note.Add("srv1", "1");
        note.Save();

        // The file stays as it was, with the note beside it: the next start leaves out what the note names.
        note.NotWritten();

        Assert.True(File.Exists(NotePath));
        Assert.Null(_time.NextDue);
    }

    /// <summary>
    /// The tests' clock, which also keeps each timer's callback, as the thread pool keeps one that is already on its way
    /// when its timer is disposed.
    /// </summary>
    private sealed class CallbacksKept(TimeProvider inner) : TimeProvider
    {
        public List<Action> Callbacks { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Callbacks.Add(() => callback(state));
            return inner.CreateTimer(callback, state, dueTime, period);
        }
    }
}
