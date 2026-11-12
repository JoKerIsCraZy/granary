using Granary.Core.Controller;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.Gaming.Input;

namespace Granary.App.Services;

/// <summary>
/// The game controllers attached to this PC, read as one (<see cref="PadReading.Merge"/>): any of them works, and one
/// that sleeps or is unplugged simply stops counting until it is back.
/// </summary>
/// <remarks>
/// Windows (<c>Windows.Gaming.Input</c>) knows an Xbox pad, and any pad its maker registered as one, as a
/// <see cref="Gamepad"/>. It fills its list a moment after it is first asked for and tells of changes on a thread
/// of its own; the list here is the UI thread's.
/// </remarks>
internal sealed class GamepadReader
{
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger _logger;
    private List<IPad> _pads = [];
    private bool _hasFailed;

    public GamepadReader(DispatcherQueue dispatcher, ILogger logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        try
        {
            Gamepad.GamepadAdded += OnPadsChanged;
            Gamepad.GamepadRemoved += OnPadsChanged;
        }
        catch (Exception ex)
        {
            // A Windows without its game input: the app starts all the same, and no pad is ever listed.
            _logger.LogWarning(ex, "Windows does not tell of game controllers here");
            return;
        }
        // Asking is what makes Windows list them.
        List();
    }

    private interface IPad
    {
        PadReading Read();
    }

    /// <summary>A pad came or went; on the UI thread.</summary>
    public event EventHandler? Changed;

    public bool HasPads => _pads.Count > 0;

    /// <summary>All pads as one; a pad at rest when there is none, or when Windows could not be asked.</summary>
    public PadReading Read()
    {
        var merged = default(PadReading);
        try
        {
            foreach (var pad in _pads)
            {
                merged = PadReading.Merge(merged, pad.Read());
            }
            _hasFailed = false;
            return merged;
        }
        catch (Exception ex)
        {
            // A pad on its way out can fail its last readings. Said once, not sixty times a second.
            if (!_hasFailed)
            {
                _hasFailed = true;
                _logger.LogWarning(ex, "A game controller could not be read");
            }
            return default;
        }
    }

    private void OnPadsChanged(object? sender, object pad) => _dispatcher.TryEnqueue(() =>
    {
        List();
        Changed?.Invoke(this, EventArgs.Empty);
    });

    private void List()
    {
        try
        {
            var pads = new List<IPad>();
            foreach (var gamepad in Gamepad.Gamepads)
            {
                pads.Add(new KnownPad(gamepad));
            }
            _pads = pads;
            _logger.LogInformation("Game controllers attached: {Count}", pads.Count);
        }
        catch (Exception ex)
        {
            // Without controllers the app works as before.
            _pads = [];
            _logger.LogWarning(ex, "The game controllers could not be listed");
        }
    }

    // A pad Windows knows as a gamepad: its buttons are already named by position.
    private sealed class KnownPad(Gamepad pad) : IPad
    {
        private static readonly (GamepadButtons From, PadButtons To)[] Buttons =
        [
            (GamepadButtons.A, PadButtons.A), (GamepadButtons.B, PadButtons.B), (GamepadButtons.X, PadButtons.X), (GamepadButtons.Y, PadButtons.Y),
            (GamepadButtons.View, PadButtons.View), (GamepadButtons.Menu, PadButtons.Menu),
            (GamepadButtons.LeftShoulder, PadButtons.LeftShoulder), (GamepadButtons.RightShoulder, PadButtons.RightShoulder),
            (GamepadButtons.DPadUp, PadButtons.DPadUp), (GamepadButtons.DPadDown, PadButtons.DPadDown),
            (GamepadButtons.DPadLeft, PadButtons.DPadLeft), (GamepadButtons.DPadRight, PadButtons.DPadRight),
            (GamepadButtons.LeftThumbstick, PadButtons.LeftStick), (GamepadButtons.RightThumbstick, PadButtons.RightStick),
        ];

        public PadReading Read()
        {
            var reading = pad.GetCurrentReading();
            var buttons = PadButtons.None;
            foreach (var (from, to) in Buttons)
            {
                if ((reading.Buttons & from) != 0)
                {
                    buttons |= to;
                }
            }
            return new PadReading(buttons, reading.LeftThumbstickX, reading.LeftThumbstickY, reading.RightThumbstickX, reading.RightThumbstickY,
                reading.LeftTrigger, reading.RightTrigger);
        }
    }
}
