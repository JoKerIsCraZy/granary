using Granary.Core.Updates;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Granary.App;

/// <summary>
/// The entry point, as the XAML compiler writes it, with Velopack first: while an install, an update or an uninstall runs
/// Granary's exe with its own arguments, Velopack does its part and exits before any window opens (docs/release.md).
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack would otherwise apply any newer package it finds in its packages folder at start. Only an update the
        // VelopackUpdater verified as signed is applied, as Granary exits.
        VelopackApp.Build().SetAutoApplyOnStartup(UpdateSafety.ApplyPackagesAtStart).Run();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(start =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
