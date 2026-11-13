namespace Granary.Core.Controller;

// Seeking in the player. Left and Right on the picture add steps, larger the longer the direction is held
// (PadTuning.SeekStep); the triggers scrub at a speed set by how far they are pulled. Either way the player first hears
// only where the seek would land (SeekPreview), and then of one seek: when a trigger is let go, or once no step was
// added for a moment, or at once when a menu opens over it. A stream, and a Watch Party's room, get one request rather
// than one for every step.
public sealed partial class PadInterpreter
{
    // Seconds from where the seek began, how far it can go back and on from there, and when it last changed.
    private bool _hasSeek;
    private double _seekOffset;
    private double _seekBackRoom;
    private double _seekForwardRoom;
    private TimeSpan _seekChanged;
    private bool _isScrubbing;

    private void StepSeek(PadDirection direction, TimeSpan held) =>
        MoveSeek(direction == PadDirection.Left
            ? -PadTuning.SeekStep(held, _context.SkipBackSeconds)
            : PadTuning.SeekStep(held, _context.SkipForwardSeconds));

    // Never past the title's start or end: held there, the way back begins at once rather than after all that was
    // counted beyond it.
    private void MoveSeek(double seconds)
    {
        if (!_hasSeek)
        {
            _hasSeek = true;
            _seekBackRoom = Math.Max(0, _context.PositionSeconds);
            _seekForwardRoom = _context.DurationSeconds > 0
                ? Math.Max(0, _context.DurationSeconds - _context.PositionSeconds)
                : double.PositiveInfinity;
        }
        _seekOffset = Math.Clamp(_seekOffset + seconds, -_seekBackRoom, _seekForwardRoom);
        _seekChanged = _now;
        _actions.Add(PadAction.Of(PadActionKind.SeekPreview, _seekOffset));
    }

    // In the player, once the buttons and the direction are taken: the triggers scrub, and a seek that is done is sent.
    private void TakeSeek(double leftPressure, double rightPressure, TimeSpan step)
    {
        if (_context.Focus == PadFocus.Menu)
        {
            // A menu opened over a seek under way: the seek is sent now, where the bar showed it, rather than held
            // back for as long as the menu stays.
            SendSeek();
        }
        else
        {
            TakeScrub(leftPressure, rightPressure, step);
        }
        SendSeekWhenDue();
    }

    private void TakeScrub(double leftPressure, double rightPressure, TimeSpan step)
    {
        bool back = _live.HasFlag(PadButtons.LeftTrigger);
        bool forward = _live.HasFlag(PadButtons.RightTrigger);
        if (!_context.CanSeek || !(back || forward))
        {
            if (_isScrubbing)
            {
                SendSeek();
            }
            return;
        }
        _isScrubbing = true;
        double rate = (forward ? PadTuning.ScrubRate(rightPressure) : 0) - (back ? PadTuning.ScrubRate(leftPressure) : 0);
        if (rate != 0 && step > TimeSpan.Zero)
        {
            MoveSeek(rate * step.TotalSeconds);
        }
    }

    // Steps wait a moment for the next one, and for a held direction to be let go.
    private void SendSeekWhenDue()
    {
        bool isStepping = _direction != PadDirection.None && _directionUse == DirectionUse.Seek;
        if (_hasSeek && !_isScrubbing && !isStepping && _now - _seekChanged >= PadTuning.SeekCommitDelay)
        {
            SendSeek();
        }
    }

    private void SendSeek()
    {
        if (_hasSeek)
        {
            _actions.Add(Math.Abs(_seekOffset) < PadTuning.SeekNothing
                ? new PadAction(PadActionKind.SeekCancel)
                : PadAction.Of(PadActionKind.SeekCommit, _seekOffset));
        }
        DropSeek();
    }

    private void DropSeek()
    {
        _hasSeek = false;
        _seekOffset = 0;
        _isScrubbing = false;
    }
}
