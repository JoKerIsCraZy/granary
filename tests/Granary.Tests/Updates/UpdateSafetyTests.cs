using Granary.Core.Updates;

namespace Granary.Tests.Updates;

/// <summary>
/// The Velopack settings signed updates rely on, and that the app hands them to Velopack. The app's project is WinUI and
/// not referenced here, so its two call sites are read as source.
/// </summary>
public sealed class UpdateSafetyTests
{
    [Fact]
    public void Velopack_NeverAppliesAPackageAtStart_AndTakesNoDeltas()
    {
        Assert.False(UpdateSafety.ApplyPackagesAtStart);
        Assert.True(UpdateSafety.MaximumDeltas < 0);
    }

    [Theory]
    [InlineData("src/Granary.App/Program.cs", "SetAutoApplyOnStartup(UpdateSafety.ApplyPackagesAtStart)")]
    [InlineData("src/Granary.App/Services/VelopackUpdater.cs", "MaximumDeltasBeforeFallback = UpdateSafety.MaximumDeltas")]
    public void TheApp_HandsTheSettingsToVelopack(string file, string call)
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), file));

        Assert.Contains(call, source, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Granary.slnx")))
            {
                return folder.FullName;
            }
        }
        throw new DirectoryNotFoundException("The tests run outside the repository.");
    }
}
