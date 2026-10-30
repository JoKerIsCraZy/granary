using System.Text;
using Granary.Core.Updates;

namespace Granary.Tests.Updates;

/// <summary>
/// The Velopack settings signed updates rely on, and that the app hands them to Velopack. The app's project is WinUI and
/// not referenced here, so its two call sites are read as source, with comments taken out first.
/// </summary>
public sealed class UpdateSafetyTests
{
    private const string ApplyCall = "SetAutoApplyOnStartup(UpdateSafety.ApplyPackagesAtStart)";
    private const string DeltasSetting = "MaximumDeltasBeforeFallback = UpdateSafety.MaximumDeltas";

    [Fact]
    public void Velopack_NeverAppliesAPackageAtStart_AndTakesNoDeltas()
    {
        Assert.False(UpdateSafety.ApplyPackagesAtStart);
        Assert.True(UpdateSafety.MaximumDeltas < 0);
    }

    [Theory]
    [InlineData("src/Granary.App/Program.cs", ApplyCall)]
    [InlineData("src/Granary.App/Services/VelopackUpdater.cs", DeltasSetting)]
    public void TheApp_HandsTheSettingsToVelopack(string file, string call)
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), file));

        Assert.True(Calls(source, call), $"{file} does not hand {call} to Velopack outside a comment.");
    }

    [Theory]
    [InlineData("// VelopackApp.Build().SetAutoApplyOnStartup(UpdateSafety.ApplyPackagesAtStart).Run();")]
    [InlineData("VelopackApp.Build()/*.SetAutoApplyOnStartup(UpdateSafety.ApplyPackagesAtStart)*/.Run();")]
    [InlineData("/*\nVelopackApp.Build().SetAutoApplyOnStartup(UpdateSafety.ApplyPackagesAtStart).Run();\n*/")]
    [InlineData("    // a note // VelopackApp.Build().SetAutoApplyOnStartup(UpdateSafety.ApplyPackagesAtStart).Run();")]
    public void ACommentedOutCall_DoesNotCount(string source)
    {
        Assert.False(Calls(source, ApplyCall));
    }

    [Theory]
    [InlineData("VelopackApp.Build().SetAutoApplyOnStartup(UpdateSafety.ApplyPackagesAtStart).Run(); // after it")]
    [InlineData("var url = \"https://example\"; VelopackApp.Build().SetAutoApplyOnStartup(UpdateSafety.ApplyPackagesAtStart).Run();")]
    [InlineData("/* before it */ VelopackApp.Build().SetAutoApplyOnStartup(UpdateSafety.ApplyPackagesAtStart).Run();")]
    public void ACallBesideAComment_StillCounts(string source)
    {
        Assert.True(Calls(source, ApplyCall));
    }

    private static bool Calls(string source, string call) => WithoutComments(source).Contains(call, StringComparison.Ordinal);

    /// <summary>
    /// C# source without its // and /* */ comments. Strings and characters are kept as they are, so a "//" inside one does
    /// not start a comment. Enough for the two files read here, which have no raw strings.
    /// </summary>
    private static string WithoutComments(string source)
    {
        var code = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && next == '*')
            {
                int close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? source.Length : close + 2;
                code.Append(' ');
            }
            else if (c is '"' or '\'')
            {
                bool verbatim = i > 0 && source[i - 1] == '@';
                int start = i++;
                while (i < source.Length && source[i] != c)
                {
                    i += !verbatim && source[i] == '\\' ? 2 : 1;
                }
                i++;
                code.Append(source, start, Math.Min(i, source.Length) - start);
            }
            else
            {
                code.Append(c);
                i++;
            }
        }
        return code.ToString();
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
