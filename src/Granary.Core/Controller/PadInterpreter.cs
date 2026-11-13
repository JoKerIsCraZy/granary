namespace Granary.Core.Controller;

/// <summary>
/// Turns what a game controller reports into what the viewer asked for. The window reads the pad, hands each reading over
/// with what it knows (<see cref="PadContext"/>) and carries out the actions that come back; nothing here knows a window.
/// </summary>
/// <remarks>
/// Buttons count by position (<see cref="PadButtons"/>). On the pages: the D-pad and the left stick move focus, A presses
/// (held, it opens the item's menu, so it acts when let go), B goes back, X plays the focused card, Y searches, Menu opens
/// the item's menu, View the sidebar, the bumpers switch tabs, the triggers page and the right stick scrolls. In the
/// player: A on the picture plays or pauses, X always does, Left and Right on the picture seek
/// (PadInterpreter.Seek.cs), Up and Down bring the bar, the bumpers jump chapters, the right stick sets the volume, Y
/// opens audio and subtitles, Menu the settings and View the playback info.
/// A stick inside its dead zone is at rest and no activity, so a stick that drifts keeps nothing awake. A button held
/// while the pad is first read, or while the pages and the player change places, does nothing until it is pressed again.
/// </remarks>
public sealed partial class PadInterpreter
{
    private const PadButtons DPad = PadButtons.DPadUp | PadButtons.DPadDown | PadButtons.DPadLeft | PadButtons.DPadRight;
    private const PadButtons Triggers = PadButtons.LeftTrigger | PadButtons.RightTrigger;

    private readonly TimeProvider _time;
    private readonly long _origin;
    private readonly List<PadAction> _actions = [];

    private TimeSpan _now;
    private TimeSpan _last;
    private PadContext _context;

    // As of the reading before: the buttons down, a trigger pulled far enough among them, and those of them that count.
    private PadButtons _down;
    private PadButtons _live;

    // Held through a reset or a change of mode: nothing until let go.
    private PadButtons _spent;
    private bool _spendsAll = true;
    private bool _isLeftStickSpent;

    private bool _isLeftTriggerDown;
    private bool _isRightTriggerDown;

    // A: when it went down, and whether the hold already opened the menu.
    private TimeSpan _aSince;
    private bool _aOpenedMenu;

    // The direction of the D-pad or the left stick, what it is used for, for how long it was read as held and when it
    // steps next.
    private PadDirection _stick;
    private PadDirection _direction;
    private DirectionUse _directionUse;
    private TimeSpan _directionHeld;
    private TimeSpan _directionNext;

    private TimeSpan _pageNext;

    // The right stick as the volume's direction.
    private PadDirection _volume;
    private TimeSpan _volumeNext;
    private bool _isRightStickSpent;

    public PadInterpreter(TimeProvider time)
    {
        _time = time;
        _origin = time.GetTimestamp();
    }

    private enum DirectionUse
    {
        /// <summary>Held through a change of where focus is: nothing until let go.</summary>
        Spent,
        Focus,
        Seek,
    }

    /// <summary>The last reading had a button down, or a stick or trigger past its dead zone: the viewer is there.</summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Nothing is held and nothing waits: the pad can be read less often, and a reading at rest (<see cref="IsAtRest"/>)
    /// need not be handed over at all.
    /// </summary>
    public bool IsIdle => !_spendsAll && !IsActive && !_hasSeek;

    /// <summary>No button is down and both sticks and triggers are inside their dead zones.</summary>
    public static bool IsAtRest(PadReading reading) =>
        (reading.Buttons & ~Triggers) == PadButtons.None
        && PadSticks.PastDeadZone(reading.LeftX, reading.LeftY, PadTuning.LeftStickDeadZone) == (0, 0)
        && PadSticks.PastDeadZone(reading.RightX, reading.RightY, PadTuning.RightStickDeadZone) == (0, 0)
        && Pressure(reading.LeftTrigger, reading.Buttons.HasFlag(PadButtons.LeftTrigger)) == 0
        && Pressure(reading.RightTrigger, reading.Buttons.HasFlag(PadButtons.RightTrigger)) == 0;

    /// <summary>
    /// Forgets what is held and a seek under way, without sending it: the pad went away, or the window lost the
    /// foreground. What is still held at the next reading does nothing until it is pressed again.
    /// </summary>
    public void Reset()
    {
        _spendsAll = true;
        _live = PadButtons.None;
        _direction = PadDirection.None;
        _volume = PadDirection.None;
        IsActive = false;
        DropSeek();
    }

    /// <summary>
    /// Takes one reading and returns what it asks for, in order. The list is reused: it holds until the next call.
    /// A reading that failed (<see cref="PadReading.HasFailed"/>) asks for nothing but the end of a seek under way.
    /// </summary>
    public IReadOnlyList<PadAction> Update(PadReading reading, PadContext context)
    {
        _actions.Clear();
        if (reading.HasFailed)
        {
            Lose();
            return _actions;
        }
        var step = TakeTime();
        var (leftX, leftY) = PadSticks.PastDeadZone(reading.LeftX, reading.LeftY, PadTuning.LeftStickDeadZone);
        var (rightX, rightY) = PadSticks.PastDeadZone(reading.RightX, reading.RightY, PadTuning.RightStickDeadZone);
        double leftTrigger = Pressure(reading.LeftTrigger, reading.Buttons.HasFlag(PadButtons.LeftTrigger));
        double rightTrigger = Pressure(reading.RightTrigger, reading.Buttons.HasFlag(PadButtons.RightTrigger));
        IsActive = !IsAtRest(reading);
        _stick = PadSticks.Direction(leftX, leftY, _stick);

        var down = ButtonsDown(reading.Buttons, leftTrigger, rightTrigger);
        Spend(down, isRightStickAtRest: rightX == 0 && rightY == 0, context.Mode);
        _down = down;
        _context = context;

        var live = down & ~_spent;
        var pressed = live & ~_live;
        var released = _live & ~live;
        _live = live;

        TakeButtons(pressed, released);
        TakeDirection(DirectionOf(live), step);
        if (context.Mode == PadMode.Browsing)
        {
            TakePaging(pressed);
            TakeScroll(reading.RightX, reading.RightY, step);
            return _actions;
        }
        if (context.Focus != PadFocus.Menu)
        {
            TakeChapters(pressed);
            TakeVolume(rightX, rightY);
        }
        TakeSeek(leftTrigger, rightTrigger, step);
        return _actions;
    }

    // The time this reading stands for, never more than LongestStep.
    private TimeSpan TakeTime()
    {
        _now = _time.GetElapsedTime(_origin);
        var step = _now - _last;
        _last = _now;
        return step > PadTuning.LongestStep ? PadTuning.LongestStep : step;
    }

    // The buttons down, a trigger pulled far enough among them.
    private PadButtons ButtonsDown(PadButtons buttons, double leftTrigger, double rightTrigger)
    {
        _isLeftTriggerDown = PadSticks.IsPressed(leftTrigger, _isLeftTriggerDown);
        _isRightTriggerDown = PadSticks.IsPressed(rightTrigger, _isRightTriggerDown);
        return (buttons & ~Triggers)
            | (_isLeftTriggerDown ? PadButtons.LeftTrigger : PadButtons.None)
            | (_isRightTriggerDown ? PadButtons.RightTrigger : PadButtons.None);
    }

    // Marks what is held through a reset or a change of mode, and takes the mark off what is no longer held. Asked
    // before this reading becomes the one before: _down and _context are still the last one's.
    private void Spend(PadButtons down, bool isRightStickAtRest, PadMode mode)
    {
        if (_spendsAll)
        {
            _spendsAll = false;
            _spent = down;
            _direction = PadDirection.None;
            _volume = PadDirection.None;
            _isLeftStickSpent = _stick != PadDirection.None;
            _isRightStickSpent = !isRightStickAtRest;
        }
        else if (mode != _context.Mode)
        {
            // The pages and the player changed places: what was held belongs to the one that went. A press that comes
            // with the change is the new one's.
            _spent |= down & _down;
            _directionUse = DirectionUse.Spent;
            _isRightStickSpent = !isRightStickAtRest;
            DropSeek();
        }
        _spent &= down;
        _live &= ~_spent;
        _isLeftStickSpent &= _stick != PadDirection.None;
        _isRightStickSpent &= !isRightStickAtRest;
    }

    // A reading that failed says nothing of the pad, so nothing was let go and nothing fires: a short A whose pad drops
    // out is no press. What was held is forgotten as by Reset, and a seek under way is called off for the seek bar.
    private void Lose()
    {
        if (_hasSeek)
        {
            Add(PadActionKind.SeekCancel);
        }
        Reset();
    }

    // How far a trigger is pulled. A pad that only says its trigger is down counts as pulled home. One that says both (a
    // PlayStation pad's L2 and R2 are buttons too, down from the first bit of the way) is taken by its pull: else every
    // touch would be a full pull.
    private static double Pressure(double pull, bool isButtonDown)
    {
        double pressure = pull > 0 ? Math.Min(pull, 1) : isButtonDown ? 1 : 0;
        return pressure < PadTuning.TriggerDeadZone ? 0 : pressure;
    }

    private void TakeButtons(PadButtons pressed, PadButtons released)
    {
        bool isPlayer = _context.Mode == PadMode.Player;
        if (isPlayer)
        {
            if (pressed.HasFlag(PadButtons.A))
            {
                Add(_context.Focus != PadFocus.Picture ? PadActionKind.Activate
                    : _context.HasPrompt ? PadActionKind.TriggerPrompt
                    : PadActionKind.PlayPause);
            }
        }
        else
        {
            TakeA(pressed, released);
        }
        AddWhen(pressed, PadButtons.B, PadActionKind.Back);
        AddWhen(pressed, PadButtons.X, isPlayer ? PadActionKind.TogglePause : PadActionKind.PlayFocused);
        AddWhen(pressed, PadButtons.Y, isPlayer ? PadActionKind.AudioAndSubtitles : PadActionKind.Search);
        AddWhen(pressed, PadButtons.View, isPlayer ? PadActionKind.PlaybackInfo : PadActionKind.ToggleSidebar);
        AddWhen(pressed, PadButtons.Menu, isPlayer ? PadActionKind.PlayerSettings : PadActionKind.ContextMenu);
        AddWhen(pressed, PadButtons.RightStick, PadActionKind.ToggleFullScreen);
        if (isPlayer)
        {
            AddWhen(pressed, PadButtons.LeftStick, PadActionKind.ToggleSubtitles);
        }
    }

    // On the pages A presses when it is let go: held, it opens the item's menu instead.
    private void TakeA(PadButtons pressed, PadButtons released)
    {
        if (pressed.HasFlag(PadButtons.A))
        {
            _aSince = _now;
            _aOpenedMenu = false;
        }
        else if (_live.HasFlag(PadButtons.A) && !_aOpenedMenu && _now - _aSince >= PadTuning.LongPress)
        {
            _aOpenedMenu = true;
            Add(PadActionKind.ContextMenu);
        }
        else if (released.HasFlag(PadButtons.A) && !_aOpenedMenu)
        {
            Add(PadActionKind.Activate);
        }
    }

    // The D-pad before the stick; of two D-pad buttons the one already going, so rolling over a corner changes nothing.
    private PadDirection DirectionOf(PadButtons live)
    {
        var dpad = live & DPad;
        if (dpad == PadButtons.None)
        {
            return _isLeftStickSpent ? PadDirection.None : _stick;
        }
        if (_direction != PadDirection.None && dpad.HasFlag(ButtonOf(_direction)))
        {
            return _direction;
        }
        return dpad.HasFlag(PadButtons.DPadUp) ? PadDirection.Up
            : dpad.HasFlag(PadButtons.DPadDown) ? PadDirection.Down
            : dpad.HasFlag(PadButtons.DPadLeft) ? PadDirection.Left
            : PadDirection.Right;
    }

    private static PadButtons ButtonOf(PadDirection direction) => direction switch
    {
        PadDirection.Up => PadButtons.DPadUp,
        PadDirection.Down => PadButtons.DPadDown,
        PadDirection.Left => PadButtons.DPadLeft,
        PadDirection.Right => PadButtons.DPadRight,
        _ => PadButtons.None,
    };

    private void TakeDirection(PadDirection direction, TimeSpan step)
    {
        if (direction != _direction)
        {
            _direction = direction;
            if (direction != PadDirection.None)
            {
                StartDirection(direction);
            }
            return;
        }
        // Counted in readings, none longer than LongestStep: after a stall a held seek steps as large as before it,
        // not as large as the stall was long.
        _directionHeld += step;
        if (direction == PadDirection.None || _directionUse == DirectionUse.Spent)
        {
            return;
        }
        // A press that brought the bar, or closed a menu, moves nothing in what has focus now.
        if (UseOf(direction) != _directionUse)
        {
            _directionUse = DirectionUse.Spent;
            return;
        }
        if (_now < _directionNext)
        {
            return;
        }
        if (_directionUse == DirectionUse.Seek)
        {
            _directionNext = _now + PadTuning.SeekRepeat;
            StepSeek(direction, _directionHeld);
        }
        else
        {
            _directionNext = _now + PadTuning.FocusRepeat;
            _actions.Add(PadAction.Move(direction, isRepeat: true));
        }
    }

    private void StartDirection(PadDirection direction)
    {
        _directionHeld = TimeSpan.Zero;
        _directionNext = _now + PadTuning.RepeatDelay;
        _directionUse = UseOf(direction);
        switch (_directionUse)
        {
            case DirectionUse.Focus:
                _actions.Add(PadAction.Move(direction));
                break;
            case DirectionUse.Seek:
                StepSeek(direction, TimeSpan.Zero);
                break;
            default:
                if (IsOnPicture && direction is PadDirection.Up or PadDirection.Down)
                {
                    Add(PadActionKind.EnterBar);
                }
                break;
        }
    }

    private bool IsOnPicture => _context is { Mode: PadMode.Player, Focus: PadFocus.Picture };

    // On the picture Left and Right seek, and Up and Down act once; everywhere else a direction moves focus.
    private DirectionUse UseOf(PadDirection direction) =>
        !IsOnPicture ? DirectionUse.Focus
        : direction is PadDirection.Left or PadDirection.Right && _context.CanSeek ? DirectionUse.Seek
        : DirectionUse.Spent;

    private void TakePaging(PadButtons pressed)
    {
        AddWhen(pressed, PadButtons.LeftShoulder, PadActionKind.PreviousTab);
        AddWhen(pressed, PadButtons.RightShoulder, PadActionKind.NextTab);
        var page = _live & Triggers;
        if ((pressed & Triggers) != PadButtons.None)
        {
            _pageNext = _now + PadTuning.RepeatDelay;
        }
        else if (page == PadButtons.None || _now < _pageNext)
        {
            return;
        }
        else
        {
            _pageNext = _now + PadTuning.PageRepeat;
        }
        // Both pulled: the one that came last.
        var trigger = (pressed & Triggers) != PadButtons.None ? pressed & Triggers : page;
        Add(trigger.HasFlag(PadButtons.RightTrigger) ? PadActionKind.PageDown : PadActionKind.PageUp);
    }

    private void TakeScroll(double x, double y, TimeSpan step)
    {
        var (pushX, pushY) = PadSticks.Push(x, y, PadTuning.RightStickDeadZone);
        if (_isRightStickSpent || (pushX == 0 && pushY == 0))
        {
            return;
        }
        double distance = PadTuning.ScrollSpeed * step.TotalSeconds;
        _actions.Add(new PadAction(PadActionKind.Scroll, X: pushX * distance, Y: pushY * distance));
    }

    private void TakeChapters(PadButtons pressed)
    {
        AddWhen(pressed, PadButtons.LeftShoulder, PadActionKind.PreviousChapter);
        AddWhen(pressed, PadButtons.RightShoulder, PadActionKind.NextChapter);
    }

    private void TakeVolume(double x, double y)
    {
        var direction = _isRightStickSpent ? PadDirection.None : PadSticks.Direction(x, y, _volume);
        if (direction is PadDirection.Left or PadDirection.Right)
        {
            direction = PadDirection.None;
        }
        if (direction != _volume)
        {
            _volume = direction;
            _volumeNext = _now + PadTuning.RepeatDelay;
        }
        else if (direction == PadDirection.None || _now < _volumeNext)
        {
            return;
        }
        else
        {
            _volumeNext = _now + PadTuning.VolumeRepeat;
        }
        if (direction != PadDirection.None)
        {
            _actions.Add(PadAction.Of(PadActionKind.Volume, direction == PadDirection.Up ? PadTuning.VolumeStep : -PadTuning.VolumeStep));
        }
    }

    private void Add(PadActionKind kind) => _actions.Add(new PadAction(kind));

    private void AddWhen(PadButtons pressed, PadButtons button, PadActionKind kind)
    {
        if (pressed.HasFlag(button))
        {
            Add(kind);
        }
    }
}
