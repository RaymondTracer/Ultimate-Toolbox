using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia;

namespace UltimateToolbox.AutoBlankScreen.Feature;

/// <summary>
/// Keeps the legacy raw-input behavior while attaching its WM_INPUT callback
/// to the host's real top-level HWND. The callback observes input only and
/// leaves ordinary message routing to Avalonia.
/// </summary>
internal sealed class WindowsRawInputIdleProvider : IIdleTimeProvider
{
    private const ushort GenericDesktopUsagePage = 0x01;
    private const ushort MouseUsage = 0x02;
    private const ushort KeyboardUsage = 0x06;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0;
    private const uint RimTypeKeyboard = 1;
    private const ushort MouseButtonActivityMask = 0x0FFF;
    private const uint WmInput = 0x00FF;
    private const int MinimumMouseMovementPixels = 8;
    private const int MinimumMouseMovementSquared = MinimumMouseMovementPixels * MinimumMouseMovementPixels;

    private readonly TopLevel _topLevel;
    private readonly Win32Properties.CustomWndProcHookCallback _windowProcedureHook;
    private long _lastActivityTick = Environment.TickCount64;
    private int _lastAcceptedCursorX;
    private int _lastAcceptedCursorY;
    private bool _hasAcceptedCursorPosition;
    private bool _hookRegistered;
    private bool _devicesRegistered;
    private bool _disposed;

    public WindowsRawInputIdleProvider()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow is not { } mainWindow)
        {
            throw new PlatformNotSupportedException("The host window is unavailable for Windows Raw Input registration.");
        }

        var platformHandle = mainWindow.TryGetPlatformHandle();
        if (platformHandle is null ||
            !string.Equals(platformHandle.HandleDescriptor, "HWND", StringComparison.OrdinalIgnoreCase) ||
            platformHandle.Handle == IntPtr.Zero)
        {
            throw new PlatformNotSupportedException("The host window does not expose a Windows HWND for Raw Input.");
        }

        _topLevel = mainWindow;
        _windowProcedureHook = HandleWindowMessage;
        if (GetCursorPos(out var cursorPosition))
        {
            _lastAcceptedCursorX = cursorPosition.X;
            _lastAcceptedCursorY = cursorPosition.Y;
            _hasAcceptedCursorPosition = true;
        }

        _devicesRegistered = RegisterDevices(platformHandle.Handle, RidevInputSink);
        if (_devicesRegistered)
        {
            Win32Properties.AddWndProcHookCallback(_topLevel, _windowProcedureHook);
            _hookRegistered = true;
            Description = "Windows Raw Input";
        }
        else
        {
            Description = "Windows GetLastInputInfo fallback";
            Error = "Raw Input registration failed; using GetLastInputInfo as the activity fallback.";
        }
    }

    public string Description { get; }

    public string? Error { get; }

    public TimeSpan? GetIdleTime()
    {
        if (_disposed)
        {
            return null;
        }

        if (_devicesRegistered)
        {
            var elapsed = Environment.TickCount64 - Interlocked.Read(ref _lastActivityTick);
            return TimeSpan.FromMilliseconds(Math.Max(0, elapsed));
        }

        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return null;
        }

        var currentTick = unchecked((uint)Environment.TickCount);
        return TimeSpan.FromMilliseconds(unchecked(currentTick - info.Tick));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hookRegistered)
        {
            Win32Properties.RemoveWndProcHookCallback(_topLevel, _windowProcedureHook);
            _hookRegistered = false;
        }

        if (_devicesRegistered)
        {
            RegisterDevices(IntPtr.Zero, RidevRemove);
            _devicesRegistered = false;
        }
    }

    private IntPtr HandleWindowMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_disposed && message == WmInput)
        {
            try
            {
                HandleRawInput(lParam);
            }
            catch
            {
                // Input observation must never interfere with Avalonia routing.
            }
        }

        return IntPtr.Zero;
    }

    private void HandleRawInput(IntPtr rawInputHandle)
    {
        uint size = (uint)Marshal.SizeOf<RawInput>();
        var bufferCapacity = size;
        var result = GetRawInputData(
            rawInputHandle,
            RidInput,
            out var input,
            ref size,
            (uint)Marshal.SizeOf<RawInputHeader>());

        if (result == uint.MaxValue ||
            result < (uint)Marshal.SizeOf<RawInputHeader>() ||
            result > bufferCapacity)
        {
            return;
        }

        if (HasMeaningfulActivity(input))
        {
            Interlocked.Exchange(ref _lastActivityTick, Environment.TickCount64);
        }
    }

    private bool HasMeaningfulActivity(RawInput input)
    {
        if (input.Header.Type == RimTypeKeyboard)
        {
            return true;
        }

        if (input.Header.Type != RimTypeMouse)
        {
            return false;
        }

        if ((input.Data.Mouse.Buttons.ButtonFlags & MouseButtonActivityMask) != 0)
        {
            RememberCursorPosition();
            return true;
        }

        if (input.Data.Mouse.LastX == 0 && input.Data.Mouse.LastY == 0)
        {
            return false;
        }

        if (!GetCursorPos(out var cursorPosition))
        {
            return false;
        }

        if (!_hasAcceptedCursorPosition)
        {
            _lastAcceptedCursorX = cursorPosition.X;
            _lastAcceptedCursorY = cursorPosition.Y;
            _hasAcceptedCursorPosition = true;
            return false;
        }

        long deltaX = cursorPosition.X - _lastAcceptedCursorX;
        long deltaY = cursorPosition.Y - _lastAcceptedCursorY;
        if ((deltaX * deltaX) + (deltaY * deltaY) < MinimumMouseMovementSquared)
        {
            return false;
        }

        _lastAcceptedCursorX = cursorPosition.X;
        _lastAcceptedCursorY = cursorPosition.Y;
        return true;
    }

    private void RememberCursorPosition()
    {
        if (GetCursorPos(out var cursorPosition))
        {
            _lastAcceptedCursorX = cursorPosition.X;
            _lastAcceptedCursorY = cursorPosition.Y;
            _hasAcceptedCursorPosition = true;
        }
    }

    private static bool RegisterDevices(IntPtr targetWindow, uint flags)
    {
        var devices = new[]
        {
            new RawInputDevice
            {
                UsagePage = GenericDesktopUsagePage,
                Usage = MouseUsage,
                Flags = flags,
                TargetWindow = targetWindow
            },
            new RawInputDevice
            {
                UsagePage = GenericDesktopUsagePage,
                Usage = KeyboardUsage,
                Flags = flags,
                TargetWindow = targetWindow
            }
        };

        return RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr TargetWindow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInput
    {
        public RawInputHeader Header;
        public RawInputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RawInputData
    {
        [FieldOffset(0)]
        public RawMouse Mouse;

        [FieldOffset(0)]
        public RawKeyboard Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouse
    {
        public ushort Flags;
        public RawMouseButtons Buttons;
        public uint RawButtons;
        public int LastX;
        public int LastY;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RawMouseButtons
    {
        [FieldOffset(0)]
        public uint Buttons;

        [FieldOffset(0)]
        public ushort ButtonFlags;

        [FieldOffset(2)]
        public ushort ButtonData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VirtualKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Tick;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices,
        uint deviceCount,
        uint deviceSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(
        IntPtr rawInput,
        uint command,
        out RawInput data,
        ref uint size,
        uint headerSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
