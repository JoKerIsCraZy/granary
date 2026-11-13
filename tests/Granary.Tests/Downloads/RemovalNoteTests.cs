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
    private readonly LogSink _logs = new();

    public RemovalNoteTests() => Directory.CreateDirectory(_folder);

    private string NotePath => Path.Combine(_folder, "entries.json.removed");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // A start of Granary: the note as the disk has it.
    private RemovalNote Open() => new(Path.Combine(_folder, "entries.json"), "the entries kept on this PC", _logs.For<RemovalNote>());

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
}
