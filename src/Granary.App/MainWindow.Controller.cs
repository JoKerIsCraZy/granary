using System.Runtime.InteropServices;
using Granary.App.Controls;
using Granary.App.Services;
using Granary.Core.Controller;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Granary.App;

// A game controller, Xbox or PlayStation (docs/ui-design.md, Game controller). In a desktop app a pad sends the window
// nothing, so the window reads it: while a pad is attached and the window is in front, a timer takes its reading
// (Services/GamepadReader.cs), PadInterpreter in Core says what the viewer asked for, and the window carries that out the
// way the remote's keys do it: on the pages in MainWindow.Controller.Pages.cs, in the full player in
// Views/PlayerPage.Controller.cs. Nothing is typed or clicked for the pad. With no pad, or behind another window, nothing
// runs at all, so a game played with the same pad never moves Granary.
public sealed partial class MainWindow
{
    // A frame while the pad is in use; at rest half as often, still often enough for the shortest press.
    private static readonly TimeSpan PadPoll = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan PadRestPoll = TimeSpan.FromMilliseconds(33);

    // Windows does not count a pad as the viewer being there: the screen saver and the display's sleep would come while
    // they browse. Their timers are set back while the pad is in use, this often at most.
    private static readonly TimeSpan PresenceEvery = TimeSpan.FromSeconds(10);
    private const uint EsSystemRequired = 0x1;
    private const uint EsDisplayRequired = 0x2;

    // Windows' virtual keys for a gamepad, VK_GAMEPAD_A to VK_GAMEPAD_RIGHT_THUMBSTICK_LEFT.
    private const int FirstPadKey = 195;
    private const int LastPadKey = 218;

    private readonly PadInterpreter _pad = new(TimeProvider.System);
    private GamepadReader _pads = null!;
    private DispatcherQueueTimer _padTimer = null!;
    private bool _isInFront;
    private bool _hasPadFailed;
    private PadMode _padMode;
    private long _presenceNoted;

    private void InitializeController()
    {
        _padTimer = DispatcherQueue.CreateTimer();
        _padTimer.Interval = PadRestPoll;
        _padTimer.IsRepeating = true;
        WeakTick.Subscribe(_padTimer, this, static window => window.ReadPad());
        _pads = new GamepadReader(DispatcherQueue, _logger);
        _pads.Changed += (_, _) => FollowPads(forget: true);
        Activated += (_, e) =>
        {
            _isInFront = e.WindowActivationState != WindowActivationState.Deactivated;
            FollowPads(forget: false);
        };
        Closed += (_, _) => _padTimer.Stop();
        Root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPadKeyDown), handledEventsToo: false);
    }

    // Should XAML ever hand a pad's buttons over as keys itself, they stop here (and in a menu or dialog, FollowPopupOf):
    // the window already acts on the pad's reading, and nothing happens twice.
    private void OnPadKeyDown(object sender, KeyRoutedEventArgs e) => e.Handled |= (int)e.Key is >= FirstPadKey and <= LastPadKey;

    // The pad is read only while there is one and the window is in front. A pad that came or went, and a window that comes
    // back, start afresh: what is held then is not a press, and a seek under way is dropped, never sent.
    private void FollowPads(bool forget)
    {
        bool reads = (_isInFront && _pads.HasPads) || HasDebugPad;
        if (!forget && reads == _padTimer.IsRunning)
        {
            return;
        }
        _pad.Reset();
        _player?.CancelPadSeek();
        if (reads && !_padTimer.IsRunning)
        {
            _padTimer.Interval = PadRestPoll;
            _padTimer.Start();
        }
        else if (!reads)
        {
            _padTimer.Stop();
        }
    }

    private void ReadPad()
    {
        try
        {
            var reading = ReadPads();
            // A pad lying on the table: nothing to decide, and nothing of the window is even looked at.
            if (_pad.IsIdle && PadInterpreter.IsAtRest(reading))
            {
                return;
            }
            var context = _player is { IsDocked: false } player ? player.PadContext : PadContext.Browsing;
            if (context.Mode != _padMode)
            {
                // The player closed or went to its bar, or opened: a seek the pad was making is dropped, here as in
                // the interpreter, so the seek bar shows the position again.
                _padMode = context.Mode;
                _player?.CancelPadSeek();
            }
            var actions = _pad.Update(reading, context);
            // By index: the list is the interpreter's own, and stands until the next reading.
            for (int index = 0; index < actions.Count; index++)
            {
                TakePad(actions[index]);
            }
            if (_pad.IsActive)
            {
                NotePresence();
            }
            var interval = _pad.IsIdle ? PadRestPoll : PadPoll;
            if (_padTimer.Interval != interval)
            {
                _padTimer.Interval = interval;
            }
            _hasPadFailed = false;
        }
        catch (Exception ex)
        {
            // A timer's tick: nothing may escape. What the pad was doing is dropped, a seek it was making with it. Said
            // once, should every reading fail.
            if (!_hasPadFailed)
            {
                _hasPadFailed = true;
                _logger.LogError(ex, "A game controller's input could not be carried out");
            }
            _pad.Reset();
            _player?.CancelPadSeek();
        }
    }

    private void TakePad(PadAction action)
    {
        // Scrolling and a seek's preview are no presses: focus stays as it shows, and a page waiting to place it goes on.
        bool isPress = action.Kind is not (PadActionKind.Scroll or PadActionKind.SeekPreview or PadActionKind.SeekCancel);
        if (isPress)
        {
            _keysPressed++;
            _lastInputWasKey = true;
            // What Granary made of the pad, for a report that a button did the wrong thing.
            _logger.LogDebug("Game controller: {Action} {Direction}", action.Kind, action.Direction);
        }
        if (action.Kind == PadActionKind.ToggleFullScreen)
        {
            _modes.ToggleFullScreen();
            return;
        }
        // A menu, flyout or dialog has the pad until it closes, in the player too.
        if (OpenPopup() is { } popup && TakePadInPopup(action, popup))
        {
            return;
        }
        if (_player is { IsDocked: false } player)
        {
            player.TakePad(action);
        }
        else
        {
            TakePadOnPages(action);
        }
    }

    // True for what the menu takes, or keeps from what lies behind it; the rest goes on.
    private bool TakePadInPopup(PadAction action, Popup popup)
    {
        var focused = FocusedElement();
        bool isInside = focused is not null && popup.Child is { } child && IsWithin(focused, child);
        switch (action.Kind)
        {
            case PadActionKind.MoveFocus:
                var way = PadControls.WayOf(action.Direction);
                if (!isInside)
                {
                    // Opened without taking focus: the first press goes in.
                    _ = FocusManager.FindFirstFocusableElement(popup.Child) is { } first && PadControls.Focus(first);
                }
                else if (!PadControls.Nudge(focused!, way))
                {
                    PadControls.Move(way, popup.Child);
                }
                return true;
            case PadActionKind.Activate:
                _ = isInside && PadControls.Press(focused!);
                return true;
            case PadActionKind.Back:
                ClosePopup(popup, focused);
                return true;
            // The triggers and the right stick go on to what the menu or dialog shows (ScrollerFor).
            case PadActionKind.Scroll or PadActionKind.PageUp or PadActionKind.PageDown:
                return false;
            default:
                // Over the pages nothing else gets past it: Y would open Search behind a dialog, View would take focus
                // out of it. The player's own buttons go on (X pauses, Y and Menu close the menu they opened).
                return !IsPlayerFull;
        }
    }

    // As the remote's Back closes them (OnPopupKeyDown).
    private static void ClosePopup(Popup popup, DependencyObject? focused)
    {
        if (FindAncestorOrSelf<ContentDialog>(focused) is { } dialog)
        {
            dialog.Hide();
        }
        else if (focused is ComboBoxItem row && ItemsControl.ItemsControlFromItemContainer(row) is ComboBox combo)
        {
            combo.IsDropDownOpen = false;
        }
        else
        {
            popup.IsOpen = false;
        }
    }

    // The menu, flyout or dialog that is open: the one focus is in, else the one on top. Tooltips take no input.
    private Popup? OpenPopup()
    {
        if (Root.XamlRoot is not { } root)
        {
            return null;
        }
        var focused = FocusedElement();
        Popup? top = null;
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
        {
            if (popup.Child is null or ToolTip)
            {
                continue;
            }
            if (focused is not null && IsWithin(focused, popup.Child))
            {
                return popup;
            }
            top ??= popup;
        }
        return top;
    }

    private void NotePresence()
    {
        long now = Environment.TickCount64;
        if (now - _presenceNoted >= PresenceEvery.TotalMilliseconds)
        {
            _presenceNoted = now;
            // Without ES_CONTINUOUS this only sets the idle timers back, as a key does; nothing is held.
            _ = SetThreadExecutionState(EsSystemRequired | EsDisplayRequired);
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);

#if DEBUG
    // The debug remote's pad (Debugging/RemoteWindow.cs): a pad at rest for a moment, then the reading for as long as it
    // is held, then at rest again so the press is let go, and then no pad. The rest before it matters: a pad's first
    // reading is no press (PadInterpreter), so a button down from the start would do nothing.
    private const long DebugPadLeadIn = 100;
    private const long DebugPadRest = 300;
    private PadReading _debugPad;
    private long _debugPadFrom;
    private long _debugPadUntil;
    private long _debugPadGone;

    private bool HasDebugPad => Environment.TickCount64 < _debugPadGone;

    /// <summary>Holds <paramref name="reading"/> for <paramref name="held"/>, as if a pad reported it.</summary>
    internal void HoldDebugPad(PadReading reading, TimeSpan held)
    {
        _debugPad = reading;
        _debugPadFrom = Environment.TickCount64 + DebugPadLeadIn;
        _debugPadUntil = _debugPadFrom + (long)held.TotalMilliseconds;
        _debugPadGone = _debugPadUntil + DebugPadRest;
        FollowPads(forget: false);
    }

    // The debug pad is read behind another window too: a script may drive the remote while something else is in front.
    private PadReading ReadPads()
    {
        var reading = _isInFront ? _pads.Read() : default;
        if (!HasDebugPad)
        {
            // Its moment is over: reading stops again unless a real pad is there.
            FollowPads(forget: false);
            return reading;
        }
        long now = Environment.TickCount64;
        return now >= _debugPadFrom && now < _debugPadUntil ? PadReading.Merge(reading, _debugPad) : reading;
    }
#else
    private static bool HasDebugPad => false;

    private PadReading ReadPads() => _pads.Read();
#endif
}
