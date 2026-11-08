using Granary.Core.ViewModels.Downloads;

namespace Granary.Core.Abstractions;

/// <summary>The download dialog, which the app shows over its window: the web's version picker with Granary's qualities.</summary>
public interface IDownloadOptionsDialog
{
    /// <summary>
    /// Shows <paramref name="options"/> until the viewer cancels or a press of Download started the download
    /// (<see cref="DownloadOptionsViewModel.StartAsync"/> answered true); one that answered false keeps the dialog open.
    /// </summary>
    Task ShowAsync(DownloadOptionsViewModel options);
}
