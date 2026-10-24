using System.ComponentModel;
using System.Runtime.InteropServices;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Granary.App.Views;

public sealed partial class ProfilePickerPage : Page
{
    private const string LockGlyph = "";
    private const int AvatarDecodeSize = 120;

    private bool _isPinDialogShown;

    public ProfilePickerPage()
    {
        ViewModel = App.Services.GetRequiredService<ProfilePickerViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += OnUnloaded;
    }

    public ProfilePickerViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LoadCommand.Execute(null);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (_isPinDialogShown)
        {
            PinDialog.Hide();
        }
        ViewModel.Dispose();
    }

    private void OnProfileClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ProfileItemViewModel profile)
        {
            ViewModel.SelectCommand.Execute(profile);
        }
    }

    // Items are announced by their container: give it the profile's name and PIN state.
    private void OnProfileContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is ProfileItemViewModel profile)
        {
            AutomationProperties.SetName(args.ItemContainer, profile.AccessibleName);
        }
    }

    // The view model owns the prompt; the dialog follows it.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProfilePickerViewModel.IsPinPromptOpen) when ViewModel.IsPinPromptOpen:
                ShowPinDialog();
                break;
            case nameof(ProfilePickerViewModel.IsPinPromptOpen) when _isPinDialogShown:
                PinDialog.Hide();
                break;
            case nameof(ProfilePickerViewModel.IsVerifyingPin):
                UpdateUnlockButton();
                break;
        }
    }

    private async void ShowPinDialog()
    {
        if (_isPinDialogShown)
        {
            return;
        }
        _isPinDialogShown = true;
        PinBox.Password = "";
        UpdateUnlockButton();
        PinDialog.XamlRoot = XamlRoot;
        try
        {
            await PinDialog.ShowAsync();
        }
        catch (COMException)
        {
            // Another dialog is open; the prompt cannot be shown now.
            ViewModel.CancelPinCommand.Execute(null);
        }
        finally
        {
            _isPinDialogShown = false;
        }
    }

    private void OnPinDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) =>
        PinBox.Focus(FocusState.Programmatic);

    private void OnPinDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        PinBox.Password = "";
        if (ViewModel.IsPinPromptOpen)
        {
            ViewModel.CancelPinCommand.Execute(null);
        }
    }

    private void OnPinDialogPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Stay open: the view model closes the prompt once the PIN is accepted.
        args.Cancel = true;
        SubmitPin();
    }

    private void OnPinBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            SubmitPin();
        }
    }

    private void OnPinBoxPasswordChanged(object sender, RoutedEventArgs e) => UpdateUnlockButton();

    private async void SubmitPin()
    {
        // The PIN goes from the box straight into the command; neither keeps it.
        string pin = PinBox.Password;
        if (pin.Length == 0 || ViewModel.IsVerifyingPin)
        {
            return;
        }
        PinBox.Password = "";
        await ViewModel.SubmitPinCommand.ExecuteAsync(pin);
        if (ViewModel.IsPinPromptOpen)
        {
            PinBox.Focus(FocusState.Programmatic);
        }
    }

    private void UpdateUnlockButton() =>
        PinDialog.IsPrimaryButtonEnabled = PinBox.Password.Length > 0 && !ViewModel.IsVerifyingPin;

    // Static because x:Bind function bindings in the item template call them without the page.
    // The avatar URL is presigned: BitmapImage fetches it without credentials, as the server requires.
    public static ImageSource? AvatarImage(Uri? avatarUrl) => avatarUrl is null
        ? null
        : new BitmapImage(avatarUrl) { DecodePixelWidth = AvatarDecodeSize, DecodePixelType = DecodePixelType.Logical };

    public static string LockBadge(bool hasPin) => hasPin ? LockGlyph : "";

    public static bool HasText(string text) => !string.IsNullOrEmpty(text);

    public static InfoBarSeverity StatusSeverity(bool hasLoadError) => hasLoadError ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
}
