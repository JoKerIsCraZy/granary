namespace Granary.Core;

/// <summary>
/// Where Granary keeps its files: servers, secrets, device id and logs. Setting <c>GRANARY_DATA_DIR</c>
/// moves all of them, so a test run or a second instance never touches the user's own data.
/// </summary>
public static class AppPaths
{
    public const string DataDirectoryVariable = "GRANARY_DATA_DIR";

    public static string DataDirectory =>
        Environment.GetEnvironmentVariable(DataDirectoryVariable) is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Granary");
}
