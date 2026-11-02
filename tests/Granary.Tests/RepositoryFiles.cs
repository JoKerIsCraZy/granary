namespace Granary.Tests;

/// <summary>
/// The repository's own files, for tests that read the app's source: its project is WinUI and not referenced here.
/// </summary>
internal static class RepositoryFiles
{
    public static string Root { get; } = FindRoot();

    /// <summary>The files under a folder of the repository with the given pattern, build output left out.</summary>
    public static IEnumerable<string> Under(string folder, string pattern) =>
        Directory.EnumerateFiles(Path.Combine(Root, folder), pattern, SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(Path.GetRelativePath(Root, file)));

    public static string Relative(string file) => Path.GetRelativePath(Root, file).Replace('\\', '/');

    private static bool IsBuildOutput(string relative) =>
        relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) is var parts
        && (parts.Contains("obj") || parts.Contains("bin"));

    private static string FindRoot()
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
