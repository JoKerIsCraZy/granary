using Granary.Core.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Granary.App.Controls;

/// <summary>
/// The keys that mean Back: Esc, a remote's Back button (Browser Back) and Backspace, which a text field keeps
/// (<see cref="BackLadder.IsBack"/>). Panels and menus close on all of them, not on Esc alone.
/// </summary>
internal static class BackKeys
{
    public static BackKey? Of(VirtualKey key) => key switch
    {
        VirtualKey.Escape => BackKey.Escape,
        VirtualKey.GoBack => BackKey.BrowserBack,
        VirtualKey.Back => BackKey.Backspace,
        _ => null,
    };

    public static bool IsBack(VirtualKey key, XamlRoot? root) => Of(key) is { } back && BackLadder.IsBack(back, IsTyping(root));

    public static bool IsTyping(XamlRoot? root) =>
        root is not null && FocusManager.GetFocusedElement(root) is TextBox or PasswordBox or RichEditBox;
}
