namespace Granary.Core.Abstractions;

/// <summary>What this build of Granary is, for Settings → About: its version, changelog, licences and folders.</summary>
public interface IAppInfo
{
    /// <summary>"0.2.0".</summary>
    string Version { get; }

    /// <summary>The repository the build comes from; null for a build that names none.</summary>
    Uri? SourceUrl { get; }

    /// <summary>The changelog shipped with the app (Markdown, as CHANGELOG.md); null when it is missing.</summary>
    string? ReadChangelog();

    string LicensePath { get; }

    string NoticesPath { get; }

    string DataDirectory { get; }

    string LogsDirectory { get; }
}
