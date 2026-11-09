using System.Text.Json;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary><c>window.json</c>: where the main window was as Granary closed, read at the next start.</summary>
public sealed class WindowPlaceFileTests : IDisposable
{
    private static readonly WindowPlace Place = new(new WindowBounds(-1700, 400, 1440, 800), 1.5, IsMaximized: true);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly LogSink _logs = new();
    private readonly ManualTimeProvider _time = new();

    private string FilePath => Path.Combine(_directory, "window.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private WindowPlaceFile Create() => new(FilePath, _logs.For<WindowPlaceFile>(), _time);

    // A temporary file as a save leaves it, written the given time ago.
    private string LeftBehind(string name, TimeSpan ago)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, "{");
        File.SetLastWriteTimeUtc(path, (_time.GetUtcNow() - ago).UtcDateTime);
        return path;
    }

    private void Write(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, json);
    }

    [Fact]
    public void AtFirst_ThereIsNoPlace_AndNothingIsLogged()
    {
        Assert.Null(Create().Load());
        Assert.Empty(_logs.Lines);
    }

    [Fact]
    public void ASavedPlace_IsReadAtTheNextStart()
    {
        Assert.True(Create().Save(Place));

        Assert.Equal(Place, Create().Load());
        Assert.Equal(["window.json"], Directory.GetFiles(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public void TheFile_SaysThePlaceInPixels_TheScale_AndWhetherItWasMaximized()
    {
        Create().Save(Place);

        using var json = JsonDocument.Parse(File.ReadAllText(FilePath));
        var root = json.RootElement;
        Assert.Equal(-1700, root.GetProperty("x").GetInt32());
        Assert.Equal(400, root.GetProperty("y").GetInt32());
        Assert.Equal(1440, root.GetProperty("width").GetInt32());
        Assert.Equal(800, root.GetProperty("height").GetInt32());
        Assert.Equal(1.5, root.GetProperty("scale").GetDouble());
        Assert.True(root.GetProperty("maximized").GetBoolean());
    }

    [Fact]
    public void AFileWithoutMaximized_IsARestoredWindow()
    {
        Write("""{ "x": 10, "y": 20, "width": 1440, "height": 900, "scale": 1 }""");

        Assert.Equal(new WindowPlace(new WindowBounds(10, 20, 1440, 900), 1, IsMaximized: false), Create().Load());
    }

    [Fact]
    public void AFileCutShort_OpensTheWindowAsWindowsPlacesIt()
    {
        Write("""{ "x": 10, "y": 20, "wid""");

        Assert.Null(Create().Load());
        Assert.Contains(_logs.Lines, line => line.StartsWith("Warning", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "x": 10, "y": 20, "height": 900, "scale": 1 }""")]
    [InlineData("""{ "x": 10, "y": 20, "width": 0, "height": 900, "scale": 1 }""")]
    [InlineData("""{ "x": 10, "y": 20, "width": 1440, "height": 900 }""")]
    [InlineData("""{ "x": 10, "y": 20, "width": 1440, "height": 900, "scale": 0 }""")]
    [InlineData("""{ "y": 20, "width": 1440, "height": 900, "scale": 1 }""")]
    [InlineData("""{ "x": 2147483000, "y": 20, "width": 1440, "height": 900, "scale": 1 }""")]
    [InlineData("""{ "x": 10, "y": 20, "width": 2147483000, "height": 900, "scale": 1 }""")]
    [InlineData("null")]
    public void AFileWithoutAUsablePlace_OpensTheWindowAsWindowsPlacesIt(string json)
    {
        Write(json);

        Assert.Null(Create().Load());
        Assert.Contains(_logs.Lines, line => line.StartsWith("Warning", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePlaceTheFileHolds_IsNotWrittenAgain()
    {
        var file = Create();
        file.Save(Place);

        // Held so that a write would fail: none is tried.
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(file.Save(Place));
        }

        Assert.Empty(_logs.Lines);
    }

    [Fact]
    public void APlaceReadAtStart_IsNotWrittenAgainUnchanged()
    {
        Create().Save(Place);
        var file = Create();
        Assert.Equal(Place, file.Load());

        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(file.Save(Place));
        }

        Assert.Empty(_logs.Lines);
    }

    [Fact]
    public async Task ASaveAsTheWindowSettles_WritesThePlace()
    {
        await Create().SaveAsync(Place);

        Assert.Equal(Place, Create().Load());
    }

    [Fact]
    public async Task TheSaveAsTheWindowCloses_IsTheOneThatStays()
    {
        var file = Create();
        var closed = Place with { Bounds = new WindowBounds(400, 250, 1300, 760) };

        // Whether the settled place is written before or not at all, the place at closing is the one on disk.
        var settled = file.SaveAsync(Place);
        file.Save(closed);
        await settled;

        Assert.Equal(closed, Create().Load());
    }

    [Fact]
    public async Task ASettleSave_DoesNotWaitForAWriteInFlight()
    {
        // The settle save is asked for on the UI thread: a write held up by a slow disk or a virus scanner must not block it.
        Create().Save(Place with { IsMaximized = false });
        var file = Create();
        var read = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var later = Place with { Bounds = new WindowBounds(400, 250, 1300, 760) };
        Task? second = null;
        bool askedAtOnce = false;
        file.BeforeMoveRetry = () =>
        {
            if (second is null)
            {
                // From another thread while this one writes: asking must return before this write ends.
                var asking = Task.Run(() => { second = file.SaveAsync(later); }, TestContext.Current.CancellationToken);
                askedAtOnce = asking.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                second ??= Task.CompletedTask;
                read.Dispose();
            }
        };

        await file.SaveAsync(Place);
        await second!;

        Assert.True(askedAtOnce);
        Assert.Equal(later, Create().Load());
    }

    [Fact]
    public async Task ASettleSaveAfterTheClose_WritesNothing()
    {
        var file = Create();
        file.Save(Place);

        await file.SaveAsync(Place with { Bounds = new WindowBounds(400, 250, 1300, 760) });

        Assert.Equal(Place, Create().Load());
    }

    [Fact]
    public void ATemporaryFileLeftByAKilledGranary_IsRemovedAtStart()
    {
        string orphan = LeftBehind($"window.json.{Guid.NewGuid():N}.tmp", TimeSpan.FromMinutes(5));

        Create().Load();

        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public void ATemporaryFileJustWritten_StaysForTheGranaryWritingIt()
    {
        string fresh = LeftBehind($"window.json.{Guid.NewGuid():N}.tmp", TimeSpan.FromSeconds(10));

        Create().Load();

        Assert.True(File.Exists(fresh));
    }

    [Theory]
    [InlineData("window.json.notes.tmp")]
    [InlineData("window.json.tmp")]
    [InlineData("sidebar.json.0123456789abcdef0123456789abcdef.tmp")]
    [InlineData("window.json.0123456789abcdef0123456789abcdef.bak")]
    public void OtherFiles_StayAtStart(string name)
    {
        string other = LeftBehind(name, TimeSpan.FromMinutes(5));

        Create().Load();

        Assert.True(File.Exists(other));
    }

    [Fact]
    public void AFileHeldOpenForAMoment_IsStillReplaced()
    {
        var file = Create();
        file.Save(Place with { IsMaximized = false });
        // As a virus scanner reads a file just written.
        using var read = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        bool triedAgain = false;
        file.BeforeMoveRetry = () =>
        {
            triedAgain = true;
            read.Dispose();
        };

        Assert.True(file.Save(Place));

        Assert.True(triedAgain);
        Assert.Equal(Place, Create().Load());
        Assert.Equal(["window.json"], Directory.GetFiles(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public void ASaveThatCannotReplaceTheFile_IsLogged_AndLeavesItAndNothingBeside()
    {
        var file = Create();
        var before = Place with { IsMaximized = false };
        file.Save(before);

        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(file.Save(Place));
        }

        Assert.Equal(before, Create().Load());
        Assert.Equal(["window.json"], Directory.GetFiles(_directory).Select(Path.GetFileName));
        Assert.Contains(_logs.Lines, line => line.StartsWith("Warning", StringComparison.Ordinal));
    }
}
