using System.Reflection;
using Granary.App.Logging;
using Granary.Core;
using Granary.Core.Abstractions;

namespace Granary.App.Services;

/// <summary>
/// What this build of Granary is (Settings → About): its version, the repository it was built for
/// (<c>-p:GranaryUpdateRepository</c>, kept in the assembly's metadata), its changelog and licences next to the exe.
/// </summary>
internal sealed class BuildInfo : IAppInfo
{
    private static readonly Assembly App = typeof(BuildInfo).Assembly;

    /// <summary>Where releases and the source are; null for a build that names none.</summary>
    public static Uri? UpdateRepository { get; } =
        App.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "GranaryUpdateRepository")?.Value is { Length: > 0 } value
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : null;

    /// <summary>"0.2.0": the informational version without the commit the SDK appends.</summary>
    public string Version { get; } = App.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? App.GetName().Version?.ToString(3) ?? "0.0.0";

    public Uri? SourceUrl => UpdateRepository;

    public string? ReadChangelog() => ReadNextToApp("CHANGELOG.md");

    public string LicensePath => Path.Combine(AppContext.BaseDirectory, "LICENSE");

    public string NoticesPath => Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md");

    public string DataDirectory => AppPaths.DataDirectory;

    public string LogsDirectory => FileLoggerProvider.DefaultDirectory;

    private static string? ReadNextToApp(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, name);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
