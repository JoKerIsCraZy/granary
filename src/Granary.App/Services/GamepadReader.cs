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
/// <see cref="Gamepad"/>. A DualShock 4 or DualSense is only a <see cref="RawGameController"/> to it, a list of buttons
/// and axes, unless a tool presents it as an Xbox pad; <see cref="SonyPad"/> reads those by position. Windows fills its
/// lists a moment after they are first asked for and tells of changes on a thread of its own; the list here is the UI
/// thread's.
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
            RawGameController.RawGameControllerAdded += OnPadsChanged;
            RawGameController.RawGameControllerRemoved += OnPadsChanged;
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
            foreach (var controller in RawGameController.RawGameControllers)
            {
                if (Gamepad.FromGameController(controller) is null && SonyPad.IsKnown(controller.HardwareVendorId, controller.HardwareProductId))
                {
                    pads.Add(new RawSonyPad(controller));
                }
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

    // A DualShock 4 or DualSense as Windows lists it by itself.
    private sealed class RawSonyPad(RawGameController controller) : IPad
    {
        private readonly SonyPad _layout = new();
        private readonly bool[] _buttons = new bool[controller.ButtonCount];
        private readonly GameControllerSwitchPosition[] _switches = new GameControllerSwitchPosition[controller.SwitchCount];
        private readonly double[] _axes = new double[controller.AxisCount];

        public PadReading Read()
        {
            controller.GetCurrentReading(_buttons, _switches, _axes);
            return _layout.Read(_buttons, _axes, _switches.Length > 0 ? DPadOf(_switches[0]) : PadButtons.None);
        }

        private static PadButtons DPadOf(GameControllerSwitchPosition position) => position switch
        {
            GameControllerSwitchPosition.Up => PadButtons.DPadUp,
            GameControllerSwitchPosition.UpRight => PadButtons.DPadUp | PadButtons.DPadRight,
            GameControllerSwitchPosition.Right => PadButtons.DPadRight,
            GameControllerSwitchPosition.DownRight => PadButtons.DPadDown | PadButtons.DPadRight,
            GameControllerSwitchPosition.Down => PadButtons.DPadDown,
            GameControllerSwitchPosition.DownLeft => PadButtons.DPadDown | PadButtons.DPadLeft,
            GameControllerSwitchPosition.Left => PadButtons.DPadLeft,
            GameControllerSwitchPosition.UpLeft => PadButtons.DPadUp | PadButtons.DPadLeft,
            _ => PadButtons.None,
        };
    }
}
