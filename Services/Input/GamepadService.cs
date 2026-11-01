using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace BagaGame.Services.Input;

/// <summary>Actions a controller can request from Big Picture.</summary>
public enum PadCommand
{
    Left,
    Right,
    Up,
    Down,
    ToggleGrid,
    Play,
    Details,
    Menu,
    Back,
}

/// <summary>
/// XInput polling without third-party libraries. XInput ships with Windows, so
/// an Xbox controller works out of the box; other pads appear once their driver
/// maps them to XInput (DS4Windows and similar).
/// </summary>
public sealed class GamepadService : IDisposable
{
    private const ushort DPadDown = 0x0002;
    private const ushort StartButton = 0x0010;
    private const ushort BackButton = 0x0020;
    private const ushort LeftShoulder = 0x0100;
    private const ushort RightShoulder = 0x0200;
    private const ushort ButtonA = 0x1000;
    private const ushort ButtonB = 0x2000;
    private const ushort ButtonX = 0x4000;
    private const ushort ButtonY = 0x8000;

    private const short StickThreshold = 16000;

    private readonly DispatcherTimer _timer;
    private ushort _previousButtons;
    private PadCommand? _direction;
    private DateTime _nextRepeat;
    private int _padIndex;
    private int _batteryTicks;
    private bool _available = true;
    private bool _disposed;

    public GamepadService(TimeSpan? pollInterval = null)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = pollInterval ?? TimeSpan.FromMilliseconds(16),
        };
        _timer.Tick += (_, _) => Poll();
    }

    public event Action<PadCommand>? Command;

    public event Action<bool>? ConnectedChanged;

    public bool IsConnected { get; private set; }

    /// <summary>Battery charge in percent, or -1 when the pad does not report it.</summary>
    public int BatteryPercent { get; private set; } = -1;

    /// <summary>True when the controller runs over USB and has no battery level.</summary>
    public bool BatteryWired { get; private set; }

    public void Start()
    {
        if (_disposed || !_available)
        {
            return;
        }

        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _previousButtons = 0;
        _direction = null;
    }

    private void Poll()
    {
        try
        {
            PollCore();
        }
        catch
        {
            // A controller glitch must never take the launcher down.
        }
    }

    private void PollCore()
    {
        if (!TryReadState(out var state))
        {
            return;
        }

        if (++_batteryTicks >= 120)
        {
            _batteryTicks = 0;
            UpdateBattery();
        }

        var now = DateTime.UtcNow;
        var buttons = state.Gamepad.Buttons;
        var pressed = (ushort)(buttons & ~_previousButtons);
        _previousButtons = buttons;

        var direction = ReadDirection(state.Gamepad.LeftThumbX, state.Gamepad.LeftThumbY);

        if (direction != _direction)
        {
            _direction = direction;

            if (direction is { } moved)
            {
                Emit(moved);
                _nextRepeat = now.AddMilliseconds(360);
            }
        }
        else if (direction is { } held && now >= _nextRepeat)
        {
            Emit(held);
            _nextRepeat = now.AddMilliseconds(130);
        }

        if ((pressed & ButtonA) != 0)
        {
            Emit(PadCommand.Play);
        }

        if ((pressed & ButtonX) != 0)
        {
            Emit(PadCommand.Details);
        }

        if ((pressed & ButtonB) != 0)
        {
            Emit(PadCommand.Back);
        }

        if ((pressed & StartButton) != 0)
        {
            Emit(PadCommand.Menu);
        }

        if ((pressed & (LeftShoulder | RightShoulder)) != 0)
        {
            Emit((pressed & RightShoulder) != 0 ? PadCommand.Right : PadCommand.Left);
        }

        // The D-pad no longer walks the covers: its Down edge is the single
        // switch between the carousel and the compact grid.
        if ((pressed & DPadDown) != 0)
        {
            Emit(PadCommand.ToggleGrid);
        }
    }

    private bool TryReadState(out XInputState state)
    {
        state = default;

        if (!_available)
        {
            return false;
        }

        try
        {
            // Prefer the pad that answered last time, then fall back to any.
            if (XInputGetState((uint)_padIndex, out state) == 0)
            {
                SetConnected(true);
                return true;
            }

            for (var index = 0; index < 4; index++)
            {
                if (index == _padIndex)
                {
                    continue;
                }

                if (XInputGetState((uint)index, out state) == 0)
                {
                    _padIndex = index;
                    SetConnected(true);
                    return true;
                }
            }

            SetConnected(false);
            return false;
        }
        catch (DllNotFoundException)
        {
            _available = false;
            SetConnected(false);
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            _available = false;
            SetConnected(false);
            return false;
        }
        catch
        {
            // A transient XInput failure: keep the service alive.
            return false;
        }
    }

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected)
        {
            return;
        }

        IsConnected = connected;

        if (connected)
        {
            UpdateBattery();
        }
        else
        {
            BatteryPercent = -1;
            BatteryWired = false;
        }

        ConnectedChanged?.Invoke(connected);
    }

    private static PadCommand? ReadDirection(short stickX, short stickY)
    {
        // Cast before Abs: Math.Abs((short)-32768) overflows and crashed the
        // window whenever a stick was pushed fully to an edge.
        var horizontalDistance = Math.Abs((int)stickX);
        var verticalDistance = Math.Abs((int)stickY);

        var horizontal = horizontalDistance > StickThreshold;
        var vertical = verticalDistance > StickThreshold;

        if (horizontal && (!vertical || horizontalDistance >= verticalDistance))
        {
            return stickX < 0 ? PadCommand.Left : PadCommand.Right;
        }

        if (vertical)
        {
            // XInput reports a positive Y for "up", the opposite of screen axes.
            return stickY > 0 ? PadCommand.Up : PadCommand.Down;
        }

        return null;
    }

    private void Emit(PadCommand command) => Command?.Invoke(command);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        Command = null;
        ConnectedChanged = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftThumbX;
        public short LeftThumbY;
        public short RightThumbX;
        public short RightThumbY;
    }

    private void UpdateBattery()
    {
        try
        {
            if (XInputGetBatteryInformation((uint)_padIndex, 0, out var battery) != 0)
            {
                BatteryPercent = -1;
                BatteryWired = false;
                return;
            }

            BatteryWired = battery.BatteryType == 1;
            BatteryPercent = battery.BatteryLevel switch
            {
                0 => 5,
                1 => 25,
                2 => 60,
                3 => 100,
                _ => -1,
            };
        }
        catch
        {
            BatteryPercent = -1;
            BatteryWired = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputBatteryInformation
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(uint index, out XInputState state);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    private static extern int XInputGetBatteryInformation(
        uint index,
        byte deviceType,
        out XInputBatteryInformation battery);
}
