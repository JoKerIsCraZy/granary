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

    public static bool IsBack(VirtualKey key, XamlRoot? root, object? source = null) =>
        Of(key) is { } back && BackLadder.IsBack(back, IsTyping(root, source));

    /// <summary>
    /// Whether a key is typing: pressed in a text field (<paramref name="source"/>, the key's original source), or with
    /// one focused. The source counts as well as the focus because a key can move the focus while it is handled: the last
    /// Backspace in a search box can hide that box, and WinUI hands the focus to the sidebar's Home entry before the key
    /// reaches the window, which then read the Backspace as Back.
    /// </summary>
    public static bool IsTyping(XamlRoot? root, object? source = null) =>
        IsTextField(source) || root is not null && IsTextField(FocusManager.GetFocusedElement(root));

    private static bool IsTextField(object? element) => element is TextBox or PasswordBox or RichEditBox;
}
