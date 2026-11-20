using Granary.App.Controls;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Views.Settings;

/// <summary>
/// Settings → Profiles (<see cref="ProfilesSettingsViewModel"/>): reads the household when first shown; New profile and Edit
/// open the profile editor in a dialog.
/// </summary>
public sealed partial class ProfilesSettingsView : UserControl, IDisposable
{
    // One editor at a time: a second press while it is open does nothing.
    private bool _editing;

    // Closes the open editor dialog when the page leaves.
    private readonly CancellationTokenSource _close = new();

    public ProfilesSettingsView()
    {
        ViewModel = ActivatorUtilities.CreateInstance<ProfilesSettingsViewModel>(App.Services);
        InitializeComponent();
        // Once: the page keeps the view, and an edit reads the list again.
        Loaded += OnFirstLoaded;
    }

    public ProfilesSettingsViewModel ViewModel { get; }

    // The settings page lets its views go as it leaves: an open editor goes with it.
    public void Dispose()
    {
        _close.Cancel();
        _close.Dispose();
    }

    // Static because x:Bind function bindings in the template call them on the type.
    public static bool CanPress(bool canDelete, bool isIdle) => canDelete && isIdle;

    private async void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        try
        {
            await ViewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            // async void: nothing may escape; the view model words what it expects.
            Log(ex, "Settings → Profiles could not load");
        }
    }

    private void OnNewProfileClick(object sender, RoutedEventArgs e) => _ = EditAsync(null);

    private void OnEditClick(object sender, RoutedEventArgs e) => _ = EditAsync((sender as FrameworkElement)?.Tag as ProfileSettingsRow);

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProfileSettingsRow row)
        {
            ViewModel.DeleteCommand.Execute(row);
        }
    }

    private async Task EditAsync(ProfileSettingsRow? row)
    {
        if (_editing || ViewModel.CreateEditor(row) is not { } editor || XamlRoot is not { } root)
        {
            return;
        }
        _editing = true;
        try
        {
            if (await ProfileEditorView.ShowAsync(editor, root, _close.Token) is { } saved)
            {
                await ViewModel.AfterSaveAsync(saved);
            }
        }
        catch (Exception ex)
        {
            Log(ex, "The profile editor failed");
        }
        finally
        {
            _editing = false;
        }
    }

    // The kind of failure only: a message can name the profile.
    private static void Log(Exception ex, string message) =>
        App.Services.GetRequiredService<ILogger<ProfilesSettingsView>>().LogWarning("{Message} ({Failure})", message, ex.GetType().Name);
}
