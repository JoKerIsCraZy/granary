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
    public void APlaceClosedInFullScreen_IsReadSoAtTheNextStart_WithTheWindowItGoesBackTo()
    {
        var closed = Place with { IsFullScreen = true };

        Assert.True(Create().Save(closed));

        Assert.Equal(closed, Create().Load());
        using var json = JsonDocument.Parse(File.ReadAllText(FilePath));
        var root = json.RootElement;
        Assert.True(root.GetProperty("full_screen").GetBoolean());
        Assert.Equal(-1700, root.GetProperty("x").GetInt32());
        Assert.Equal(1440, root.GetProperty("width").GetInt32());
        Assert.True(root.GetProperty("maximized").GetBoolean());
    }

    [Fact]
    public void LeavingFullScreen_IsWritten_ThoughTheWindowsPlaceIsTheSame()
    {
        var file = Create();
        file.Save(Place with { IsFullScreen = true });

        Assert.True(file.Save(Place));

        Assert.Equal(Place, Create().Load());
        using var json = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.False(json.RootElement.GetProperty("full_screen").GetBoolean());
    }

    [Theory]
    [InlineData("""{ "x": 10, "y": 20, "width": 1440, "height": 900, "scale": 1 }""")]
    [InlineData("""{ "x": 10, "y": 20, "width": 1440, "height": 900, "scale": 1, "full_screen": false }""")]
    [InlineData("""{ "x": 10, "y": 20, "width": 1440, "height": 900, "scale": 1, "full_screen": "yes" }""")]
    [InlineData("""{ "x": 10, "y": 20, "width": 1440, "height": 900, "scale": 1, "full_screen": 1 }""")]
    [InlineData("""{ "x": 10, "y": 20, "width": 1440, "height": 900, "scale": 1, "full_screen": null }""")]
    [InlineData("""{ "full_screen": { "on": true, "x": 99 }, "x": 10, "y": 20, "width": 1440, "height": 900, "scale": 1 }""")]
    [InlineData("""{ "full_screen": [true, [true], { "x": 99 }], "x": 10, "y": 20, "width": 1440, "height": 900, "scale": 1 }""")]
    public void AFullScreenNeitherTrueNorFalse_OrMissing_OpensAsAWindow_WhereItWas(string json)
    {
        Write(json);

        Assert.Equal(new WindowPlace(new WindowBounds(10, 20, 1440, 900), 1, IsMaximized: false), Create().Load());
        Assert.Empty(_logs.Lines);
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
    [InlineData("""{ "x": 10, "y": 20, "width": 1440, "height": 900, "scale": 1E-300 }""")]
    [InlineData("""{ "x": 10, "y": 20, "width": 1440, "height": 900, "scale": 0.49 }""")]
    [InlineData("""{ "x": 10, "y": 20, "width": 1440, "height": 900, "scale": 5.01 }""")]
    [InlineData("""{ "x": -2147483648, "y": 20, "width": 1440, "height": 900, "scale": 1 }""")]
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

    [Theory]
    [InlineData(0.5)]
    [InlineData(5.0)]
    public void ScalesWindowsCanHave_AreRead_UpToTheirEdges(double scale)
    {
        var place = Place with { Scale = scale };
        Assert.True(Create().Save(place));

        Assert.Equal(place, Create().Load());
    }

    [Fact]
    public void AFileOverItsSmallLimit_IsNotRead()
    {
        // A window's place is a few lines; something else, or a damaged file, may be anything.
        Write("""{ "x": 10, "y": 20, "width": 1440, "height": 900, "scale": 1 }""" + new string(' ', 64 * 1024));

        Assert.Null(Create().Load());
        Assert.Contains(_logs.Lines, line => line.StartsWith("Warning", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(10, 20, 0, 900, 1.5)]
    [InlineData(10, 20, 1440, -1, 1.5)]
    [InlineData(70000, 20, 1440, 900, 1.5)]
    [InlineData(int.MinValue, 20, 1440, 900, 1.5)]
    [InlineData(10, 20, 1440, 70000, 1.5)]
    [InlineData(10, 20, 1440, 900, 0.49)]
    [InlineData(10, 20, 1440, 900, 5.01)]
    [InlineData(10, 20, 1440, 900, double.NaN)]
    public async Task APlaceTheReaderWouldReject_IsNotWritten(int x, int y, int width, int height, double scale)
    {
        var file = Create();
        var place = new WindowPlace(new WindowBounds(x, y, width, height), scale, IsMaximized: false);

        await file.SaveAsync(place);
        Assert.False(file.Save(place));

        Assert.False(File.Exists(FilePath));
        Assert.Contains(_logs.Lines, line => line.StartsWith("Warning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSaveAsTheWindowCloses_GivesUpOnAWriteHeldTooLong()
    {
        // A settle write stalled on a slow or network data folder must not keep the window from closing.
        Create().Save(Place with { IsMaximized = false });
        var file = Create();
        file.CloseWait = TimeSpan.Zero;
        var read = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        bool? closed = null;
        file.BeforeMoveRetry = () =>
        {
            if (closed is null)
            {
                // From another thread while this one writes, as the UI thread closes the window.
                bool saved = true;
                var closing = Task.Run(() => { saved = file.Save(Place with { Bounds = new WindowBounds(400, 250, 1300, 760) }); },
                    TestContext.Current.CancellationToken);
                closed = !closing.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken) || saved;
                read.Dispose();
            }
        };

        await file.SaveAsync(Place);

        Assert.False(closed);
        Assert.Equal(Place, Create().Load());
        Assert.Contains(_logs.Lines, line => line.StartsWith("Warning", StringComparison.Ordinal) && line.Contains("closing", StringComparison.Ordinal));
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
