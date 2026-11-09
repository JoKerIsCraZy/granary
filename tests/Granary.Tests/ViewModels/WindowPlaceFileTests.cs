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

    private string FilePath => Path.Combine(_directory, "window.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private WindowPlaceFile Create() => new(FilePath, _logs.For<WindowPlaceFile>());

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
