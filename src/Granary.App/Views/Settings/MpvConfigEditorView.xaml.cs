using Granary.App.Controls;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Granary.App.Views.Settings;

/// <summary>
/// The mpv.conf editor, shown by Settings in place of the Playback page. It is usable with a remote: focus starts in the
/// text, Down on its last line reaches Save, Up on its first line the way back, and Back closes it, asking first when
/// the edits were not saved.
/// </summary>
public sealed partial class MpvConfigEditorView : UserControl
{
    private static readonly char[] LineBreaks = ['\r', '\n'];

    private bool _closing;

    public MpvConfigEditorView()
    {
        ViewModel = ActivatorUtilities.CreateInstance<MpvConfigEditorViewModel>(App.Services);
        InitializeComponent();
        // Before the window's Back ladder, which would leave Settings.
        AddHandler(KeyDownEvent, new KeyEventHandler(OnBackKeyDown), handledEventsToo: false);
        Loaded += OnFirstLoaded;
    }

    public MpvConfigEditorViewModel ViewModel { get; }

    /// <summary>Raised once the editor is done, saved or left; <see cref="ClosedWith"/> says how focus goes back.</summary>
    public event EventHandler? Closed;

    /// <summary>Keyboard when a key closed the editor, so the page shows where focus lands; programmatic after a click.</summary>
    public FocusState ClosedWith { get; private set; } = FocusState.Programmatic;

    private async void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        await SettingsViewLoad.RunAsync(_ => ViewModel.LoadAsync());
        if (ViewModel.CanEdit)
        {
            Editor.Focus(FocusState.Keyboard);
            Editor.Select(0, 0);
        }
        else
        {
            BackButton.Focus(FocusState.Keyboard);
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await ViewModel.SaveAsync())
            {
                Close(SaveButton.FocusState);
            }
        }
        catch (Exception ex)
        {
            // async void: nothing may escape. A write that fails is said by the view model.
            Logger().LogError(ex, "mpv.conf could not be saved");
        }
    }

    private async void OnCancelClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await TryCloseAsync(((Control)sender).FocusState);
        }
        catch (Exception ex)
        {
            Logger().LogError(ex, "The mpv.conf editor could not close");
        }
    }

    // Esc and a remote's Back close the editor from anywhere in it; Backspace only outside the text.
    private async void OnBackKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.WasKeyDown || !BackKeys.IsBack(e.Key, XamlRoot))
        {
            return;
        }
        e.Handled = true;
        try
        {
            await TryCloseAsync(FocusState.Keyboard);
        }
        catch (Exception ex)
        {
            Logger().LogError(ex, "The mpv.conf editor could not close");
        }
    }

    // Unsaved edits are asked about first; the viewer may stay.
    private async Task TryCloseAsync(FocusState with)
    {
        if (_closing)
        {
            return;
        }
        _closing = true;
        try
        {
            if (await ViewModel.ConfirmDiscardAsync())
            {
                Close(with);
            }
        }
        finally
        {
            _closing = false;
        }
    }

    private void Close(FocusState with)
    {
        ClosedWith = with == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    // A remote has no Tab: Down on the text's last line goes on to Save, Up on its first line to the way back. Shift keeps
    // the arrows for selecting.
    private void OnEditorPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Down or VirtualKey.Up)
            || InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down))
        {
            return;
        }
        string text = Editor.Text;
        bool atEdge = e.Key == VirtualKey.Down
            ? text.IndexOfAny(LineBreaks, Editor.SelectionStart + Editor.SelectionLength) < 0
            : Editor.SelectionStart == 0 || text.LastIndexOfAny(LineBreaks, Editor.SelectionStart - 1) < 0;
        if (atEdge)
        {
            Control next = e.Key == VirtualKey.Up ? BackButton : SaveButton.IsEnabled ? SaveButton : CancelButton;
            e.Handled = next.Focus(FocusState.Keyboard);
        }
    }

    private static ILogger Logger() => App.Services.GetRequiredService<ILoggerFactory>().CreateLogger<MpvConfigEditorView>();
}
