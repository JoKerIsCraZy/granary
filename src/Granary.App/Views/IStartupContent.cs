namespace Granary.App.Views;

/// <summary>
/// A first page the start screen waits for (MainWindow.ShowStartup): it covers the app until <see cref="Ready"/>, at most
/// a few seconds. Other pages are ready once they show.
/// </summary>
internal interface IStartupContent
{
    /// <summary>Done when the page has what it shows, or has failed to get it.</summary>
    Task Ready { get; }
}
