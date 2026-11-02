#if DEBUG
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace Granary.App.Debugging;

/// <summary>
/// A TV remote on screen, to try the app's remote handling without one (debug builds, started with <c>--remote</c>). Its
/// buttons send what a PC remote sends: the arrows, Enter for OK, Browser Back, Esc and Backspace as key messages to the
/// main window's input window, so the main window keeps its focus; Play/Pause and the track keys as system media keys.
/// The window does not take activation (and gives it back if it gets it), and a held arrow repeats as a held key does.
/// </summary>
internal sealed partial class RemoteWindow : Window
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const int GwlExStyle = -20;
    private const long ExNoActivate = 0x08000000;
    private const uint KeyEventExtended = 0x0001;
    private const uint KeyEventUp = 0x0002;

    private static readonly TimeSpan ActivationSettle = TimeSpan.FromMilliseconds(80);

    private readonly Window _main;
    private readonly Queue<(uint Message, uint Key, bool Repeat)> _pending = new();
    private bool _sending;
    private readonly TextBlock _focus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };

    public RemoteWindow(Window main)
    {
        _main = main;
        Title = "TV remote";
        Content = Build();
        var hwnd = WindowNative.GetWindowHandle(this);
        SetWindowLongPtr(hwnd, GwlExStyle, (nint)(GetWindowLongPtr(hwnd, GwlExStyle) | ExNoActivate));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
        }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(300, 520));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X + area.Width - 320, area.Y + area.Height - 540));
        FocusManager.GotFocus += OnFocusMoved;
        Closed += (_, _) => FocusManager.GotFocus -= OnFocusMoved;
        main.Closed += (_, _) => Close();
    }

    /// <summary>Shows the remote without taking activation from the main window.</summary>
    public void Show() => AppWindow.Show(activateWindow: false);

    private Grid Build()
    {
        var root = new StackPanel { Spacing = 14, Padding = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = "TV remote", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

        var pad = new Grid { ColumnSpacing = 8, RowSpacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < 3; i++)
        {
            pad.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            pad.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
        }
        Place(pad, Key("▲", "Up", 0x26, repeats: true), 0, 1);
        Place(pad, Key("◀", "Left", 0x25, repeats: true), 1, 0);
        Place(pad, Key("OK", "OK (Enter)", 0x0D), 1, 1);
        Place(pad, Key("▶", "Right", 0x27, repeats: true), 1, 2);
        Place(pad, Key("▼", "Down", 0x28, repeats: true), 2, 1);
        root.Children.Add(pad);

        root.Children.Add(Row(Key("Back", "Back (Browser Back)", 0xA6), Key("Esc", "Esc", 0x1B), Key("⌫", "Backspace", 0x08)));
        root.Children.Add(Row(Key("☰", "Menu (context key)", 0x5D), Key("Home", "Home", 0x24), Key("End", "End", 0x23)));
        root.Children.Add(Row(Media("⏮", "Previous track", 0xB1), Media("⏯", "Play/Pause", 0xB3), Media("⏭", "Next track", 0xB0)));
        root.Children.Add(_focus);
        return new Grid { Children = { root }, Background = (Brush)Application.Current.Resources["SiloBackgroundBrush"] };
    }

    private static void Place(Grid grid, FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static StackPanel Row(params FrameworkElement[] keys)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var key in keys)
        {
            key.Width = 72;
            key.Height = 44;
            row.Children.Add(key);
        }
        return row;
    }

    // A key the main window gets as a key: once per click, and while an arrow is held, again as a held key repeats.
    private ButtonBase Key(string label, string name, uint key, bool repeats = false)
    {
        // A held arrow repeats after 700 ms: a slow click must not move twice.
        ButtonBase button = repeats ? new RepeatButton { Delay = 700, Interval = 110 } : new Button();
        button.Content = label;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.VerticalAlignment = VerticalAlignment.Stretch;
        button.IsTabStop = false;
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        bool held = false;
        button.Click += (_, _) =>
        {
            Send(WmKeyDown, key, repeat: held);
            if (repeats && button.IsPressed)
            {
                held = true;
            }
            else
            {
                Send(WmKeyUp, key, repeat: true);
                held = false;
            }
        };
        if (repeats)
        {
            button.RegisterPropertyChangedCallback(ButtonBase.IsPressedProperty, (_, _) =>
            {
                if (!button.IsPressed && held)
                {
                    Send(WmKeyUp, key, repeat: true);
                    held = false;
                }
            });
        }
        return button;
    }

    // Media keys go to Windows' media session, as a remote's do, wherever focus is.
    private static Button Media(string label, string name, byte key)
    {
        var button = new Button { Content = label, IsTabStop = false };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) =>
        {
            KeybdEvent(key, 0, KeyEventExtended, 0);
            KeybdEvent(key, 0, KeyEventExtended | KeyEventUp, 0);
        };
        return button;
    }

    private void Send(uint message, uint key, bool repeat)
    {
        _pending.Enqueue((message, key, repeat));
        if (!_sending)
        {
            SendPending();
        }
    }

    // Keys reach the focused element only in the active window. If this one took activation, the main window gets it
    // back first, and the keys wait until XAML has put its focus back: sent at once, they went to no element. Waiting
    // keys go in their order.
    private async void SendPending()
    {
        _sending = true;
        try
        {
            var main = WindowNative.GetWindowHandle(_main);
            if (GetForegroundWindow() != main)
            {
                _ = SetForegroundWindow(main);
                await Task.Delay(ActivationSettle);
            }
            var site = InputSite(main);
            while (_pending.TryDequeue(out var press))
            {
                if (site != 0)
                {
                    uint scan = MapVirtualKeyW(press.Key, 0);
                    long flags = 1 | ((long)scan << 16) | (press.Repeat ? 1L << 30 : 0) | (press.Message == WmKeyUp ? 1L << 31 : 0);
                    PostMessageW(site, press.Message, (nint)press.Key, (nint)flags);
                }
            }
        }
        finally
        {
            _sending = false;
        }
    }

    // WinUI takes the keyboard in a child window of the main window's, a level or two down.
    private static nint InputSite(nint parent)
    {
        if (FindWindowExW(parent, 0, "InputSiteWindowClass", null) is var site && site != 0)
        {
            return site;
        }
        for (nint child = FindWindowExW(parent, 0, null, null); child != 0; child = FindWindowExW(parent, child, null, null))
        {
            if (InputSite(child) is var found && found != 0)
            {
                return found;
            }
        }
        return 0;
    }

    // What has focus in the main window, by the name screen readers hear.
    private void OnFocusMoved(object? sender, FocusManagerGotFocusEventArgs args)
    {
        if (args.NewFocusedElement is UIElement element && ReferenceEquals(element.XamlRoot, _main.Content?.XamlRoot))
        {
            string name = AutomationProperties.GetName(element);
            _focus.Text = $"Focus: {(string.IsNullOrEmpty(name) ? element.GetType().Name : name)}";
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static partial uint MapVirtualKeyW(uint code, uint mapType);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(nint parent, nint after, string? className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "keybd_event")]
    private static partial void KeybdEvent(byte key, byte scan, uint flags, nuint extraInfo);
}
#endif
