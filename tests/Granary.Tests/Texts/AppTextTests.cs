using System.Text.RegularExpressions;
using Granary.Core.Texts;

namespace Granary.Tests.Texts;

/// <summary>
/// The texts' entry point for XAML and view models. The process has one language, which the app applies at start-up;
/// tests never apply one, so every test sees English, and a test that wants another language uses it for its own async
/// flow only, out of the way of the tests running beside it.
/// </summary>
public sealed partial class AppTextTests
{
    [Fact]
    public void Tests_SeeEnglish()
    {
        Assert.Equal("en", AppText.Language.Tag);
    }

    [Fact]
    public void ALanguageInUse_ServesEveryOverload()
    {
        using var _ = AppText.Use(TextFixtures.Language("de"));

        Assert.Equal("Abbrechen", AppText.Get("common.cancel"));
        Assert.Equal("Weil du Krimis magst", AppText.Get("home.section.becauseYouEnjoy", "genres", "Krimis"));
        Assert.Equal("In 3 Sammlungen", AppText.Get("web.collections.copy.inCollections", "count", 3, "count_text", "3"));
        Assert.Equal("In 3 Sammlungen", AppText.Get("web.collections.copy.inCollections", ("count", 3), ("count_text", "3")));
        Assert.Equal("Elena", Assert.Single(AppText.Runs("web.audiobooks.player.narratedBy", ("narrator", "Elena")), run => run.Tags.Count == 1).Text);
    }

    [Fact]
    public void Capitals_FollowTheLanguageOnScreen()
    {
        Assert.Equal("YOUR STUFF", AppText.Upper(AppText.Get("web.shell.sidebar.yourStuff")));

        using var _ = AppText.Use(TextLanguage.Load("de"));
        Assert.Equal("de", AppText.Language.Culture.Name);
        Assert.Equal("DEINE INHALTE", AppText.Upper(AppText.Get("web.shell.sidebar.yourStuff")));
    }

    [Fact]
    public void ANumber_IsGroupedAsTheLanguageOnScreenWritesIt()
    {
        Assert.Equal("1,234", AppText.Number(1234));
        Assert.Equal("7", AppText.Number(7));

        using var _ = AppText.Use(TextLanguage.Load("de"));
        Assert.Equal("1.234", AppText.Number(1234));
    }

    [Fact]
    public async Task ALanguageInUse_StaysInItsOwnFlow_AndEndsWithIt()
    {
        Task<string> elsewhere;
        using (AppText.Use(TextFixtures.Language("de")))
        {
            Assert.Equal("de", AppText.Language.Tag);
            await Task.Yield();
            Assert.Equal("de", AppText.Language.Tag);

            // A flow that did not start in this one, as a test running beside it.
            using (ExecutionContext.SuppressFlow())
            {
                elsewhere = Task.Run(() => AppText.Language.Tag, TestContext.Current.CancellationToken);
            }
            Assert.Equal("en", await elsewhere);
        }

        Assert.Equal("en", AppText.Language.Tag);
    }

    [Fact]
    public void OnlyTheApp_AppliesALanguage()
    {
        // Applying one changes it for every test running at the time; tests use one for their own flow instead.
        string[] allowed = ["src/Granary.App/App.xaml.cs"];
        var found = RepositoryFiles.Under("src", "*.cs").Concat(RepositoryFiles.Under("tests", "*.cs"))
            .Select(file => (File: RepositoryFiles.Relative(file), Code: File.ReadAllText(file)))
            .Where(source => !allowed.Contains(source.File) && source.File != "tests/Granary.Tests/Texts/AppTextTests.cs")
            .Where(source => Apply().IsMatch(source.Code))
            .Select(source => source.File)
            .ToList();

        Assert.Empty(found);
    }

    [GeneratedRegex(@"\bAppText\s*\.\s*Apply\s*\(")]
    private static partial Regex Apply();
}
