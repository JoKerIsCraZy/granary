using Granary.Core.Abstractions;
using Granary.Core.ViewModels.Downloads;

namespace Granary.Core.Downloads;

/// <summary>
/// The download dialog until the app registers its own (before <c>AddGranaryCore</c>): it shows nothing and starts nothing,
/// so the services resolve without it.
/// </summary>
public sealed class NoDownloadOptionsDialog : IDownloadOptionsDialog
{
    public Task ShowAsync(DownloadOptionsViewModel options) => Task.CompletedTask;
}
