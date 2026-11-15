using Granary.Core.Texts;
using Granary.Tests.Fakes;

namespace Granary.Tests.Texts;

/// <summary>
/// The app language kept on this PC for the next start, as Silo's apps keep the one they last applied: the start reads it
/// before the first window, so it never fails, whatever the file holds.
/// </summary>
public sealed class AppLanguageFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly LogSink _logs = new();

    private string FilePath => Path.Combine(_directory, "app-language.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private AppLanguageFile Create() => new(FilePath, _logs.For<AppLanguageFile>());

    [Fact]
    public void WithoutAFile_TheStartFollowsWindows()
    {
        var file = Create();

        Assert.Null(file.AtStart);
        Assert.Null(file.Kept);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task AKeptLanguage_IsWhatTheNextStartReads()
    {
        var file = Create();

        await file.KeepAsync("de");

        Assert.Null(file.AtStart);
        Assert.Equal("de", file.Kept);
        Assert.Equal("de", Create().AtStart);
    }

    [Fact]
    public async Task FollowingTheDeviceAgain_IsKeptToo()
    {
        await Create().KeepAsync("fr");
        var file = Create();

        await file.KeepAsync(null);

        Assert.Equal("fr", file.AtStart);
        Assert.Null(Create().AtStart);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "app_language": 5 }""")]
    [InlineData("""{ "app_language": "" }""")]
    [InlineData("[]")]
    public void AFileThatCannotBeRead_FollowsWindows(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, content);

        Assert.Null(Create().AtStart);
    }

    [Fact]
    public void ATooLongValue_IsNoLanguage()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, $$"""{ "app_language": "{{new string('a', 200)}}" }""");

        Assert.Null(Create().AtStart);
    }

    [Fact]
    public async Task ASaveThatFailed_IsTriedAgain_ByTheNextKeep()
    {
        // A folder where the file goes: the move cannot replace it.
        Directory.CreateDirectory(FilePath);
        var file = Create();

        await file.KeepAsync("de");

        Assert.Null(file.Kept);
        Directory.Delete(FilePath);

        await file.KeepAsync("de");

        Assert.Equal("de", file.Kept);
        Assert.Equal("de", Create().AtStart);
    }
}
