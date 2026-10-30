using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;

namespace Granary.Core.ViewModels;

/// <summary>
/// Settings → About, as the apps' About (Version, Acknowledgements) with Granary's own: the version, Check for updates and
/// Automatic updates (<see cref="Updates"/>), what a ready update changes, the changelog shipped with the app, the licence
/// and third-party notices, the source, and the folders Granary keeps its data and logs in.
/// </summary>
public sealed partial class AboutViewModel : ObservableObject
{
    private readonly IAppInfo _app;

    public AboutViewModel(IAppInfo app, AppUpdatesViewModel updates)
    {
        _app = app;
        Updates = updates;
        Releases = Changelog.Parse(app.ReadChangelog());
        Updates.PropertyChanged += OnUpdatesChanged;
    }

    public AppUpdatesViewModel Updates { get; }

    /// <summary>"Version 0.2.0".</summary>
    public string VersionText => $"Version {_app.Version}";

    /// <summary>The changelog shipped with the app, newest first.</summary>
    public IReadOnlyList<ChangelogRelease> Releases { get; }

    public bool HasChangelog => Releases.Count > 0;

    /// <summary>What the downloaded release changes, by group; empty without notes.</summary>
    public IReadOnlyList<ChangelogGroup> ReadyChanges => [.. Changelog.Parse(Updates.ReleaseNotes).SelectMany(release => release.Groups)];

    public bool HasReadyChanges => ReadyChanges.Count > 0;

    /// <summary>"What's new in 0.3.0".</summary>
    public string ReadyChangesTitle => $"What's new in {Updates.ReadyVersion}";

    public Uri? SourceUrl => _app.SourceUrl;

    /// <summary>
    /// The GitHub page of the release found with automatic updates off (<c>…/releases/tag/v0.3.0</c>, as the release
    /// workflow tags it), for what it changes before anything is downloaded; null otherwise.
    /// </summary>
    public Uri? AvailableReleaseUrl => SourceUrl is { } source && Updates.AvailableVersion is { Length: > 0 } version
        ? new Uri($"{source.AbsoluteUri.TrimEnd('/')}/releases/tag/v{Uri.EscapeDataString(version)}")
        : null;

    public bool HasAvailableReleaseUrl => AvailableReleaseUrl is not null;

    public bool HasSource => SourceUrl is not null;

    public string LicensePath => _app.LicensePath;

    public string NoticesPath => _app.NoticesPath;

    public string DataDirectory => _app.DataDirectory;

    public string LogsDirectory => _app.LogsDirectory;

    /// <summary>The page left; the updates outlive it.</summary>
    public void Detach() => Updates.PropertyChanged -= OnUpdatesChanged;

    private void OnUpdatesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppUpdatesViewModel.ReleaseNotes) or nameof(AppUpdatesViewModel.ReadyVersion))
        {
            OnPropertyChanged(nameof(ReadyChanges));
            OnPropertyChanged(nameof(HasReadyChanges));
            OnPropertyChanged(nameof(ReadyChangesTitle));
        }
        if (e.PropertyName is nameof(AppUpdatesViewModel.AvailableVersion))
        {
            OnPropertyChanged(nameof(AvailableReleaseUrl));
            OnPropertyChanged(nameof(HasAvailableReleaseUrl));
        }
    }
}
