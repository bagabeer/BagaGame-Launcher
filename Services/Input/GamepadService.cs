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
    private const ushort DPadUp = 0x0001;
    private const ushort DPadDown = 0x0002;
    private const ushort DPadLeft = 0x0004;
    private const ushort DPadRight = 0x0008;
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
    private bool _available = true;
    private bool _disposed;

    public GamepadService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _timer.Tick += (_, _) => Poll();
    }

    public event Action<PadCommand>? Command;

    public event Action<bool>? ConnectedChanged;

    public bool IsConnected { get; private set; }

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

        var now = DateTime.UtcNow;
        var buttons = state.Gamepad.Buttons;
        var pressed = (ushort)(buttons & ~_previousButtons);
        _previousButtons = buttons;

        var direction = ReadDirection(buttons, state.Gamepad.LeftThumbX, state.Gamepad.LeftThumbY);

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
        ConnectedChanged?.Invoke(connected);
    }

    private static PadCommand? ReadDirection(ushort buttons, short stickX, short stickY)
    {
        if ((buttons & DPadLeft) != 0)
        {
            return PadCommand.Left;
        }

        if ((buttons & DPadRight) != 0)
        {
            return PadCommand.Right;
        }

        if ((buttons & DPadUp) != 0)
        {
            return PadCommand.Up;
        }

        if ((buttons & DPadDown) != 0)
        {
            return PadCommand.Down;
        }

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
            return stickY < 0 ? PadCommand.Up : PadCommand.Down;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(uint index, out XInputState state);
}
