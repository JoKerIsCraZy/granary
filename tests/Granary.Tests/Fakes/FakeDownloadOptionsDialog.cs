using Granary.Core.Abstractions;
using Granary.Core.ViewModels.Downloads;

namespace Granary.Tests.Fakes;

/// <summary>Keeps every download dialog it was asked to show; <see cref="WhileShown"/> plays the viewer while it is open.</summary>
internal sealed class FakeDownloadOptionsDialog : IDownloadOptionsDialog
{
    public List<DownloadOptionsViewModel> Shown { get; } = [];

    /// <summary>What the viewer does in the dialog, such as pressing Download (<c>options.StartAsync()</c>).</summary>
    public Func<DownloadOptionsViewModel, Task>? WhileShown { get; set; }

    public async Task ShowAsync(DownloadOptionsViewModel options)
    {
        Shown.Add(options);
        if (WhileShown is { } whileShown)
        {
            await whileShown(options);
        }
    }
}
