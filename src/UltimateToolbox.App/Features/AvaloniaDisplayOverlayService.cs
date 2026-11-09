using System.Runtime.InteropServices;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using UltimateToolbox.Contracts;

namespace UltimateToolbox;

internal sealed class AvaloniaDisplayOverlayService : IDisplayOverlayService
{
    private readonly Func<Window?> _mainWindowProvider;
    private readonly HashSet<OverlayHandle> _overlays = [];
    private Screens? _screens;
    private IDisposable? _cursorVisibility;
    private int _cursorVisibilityLeaseCount;
    private bool _disposed;

    public AvaloniaDisplayOverlayService(Func<Window?> mainWindowProvider)
    {
        _mainWindowProvider = mainWindowProvider;
    }

    public event EventHandler? DisplaysChanged;

    public IReadOnlyList<FeatureDisplay> GetDisplays()
    {
        Dispatcher.UIThread.VerifyAccess();
        var screens = GetScreens();
        TrackScreenChanges(screens);

        var raw = screens.All;
        var counts = raw
            .Select(screen => screen.DisplayName?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .GroupBy(name => name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return raw.Select((screen, index) =>
        {
            var name = screen.DisplayName?.Trim();
            var id = string.IsNullOrWhiteSpace(name)
                ? $"display-{index + 1}"
                : counts[name] > 1
                    ? $"{name}#{index + 1}"
                    : name;
            return new FeatureDisplay(
                id,
                string.IsNullOrWhiteSpace(name) ? null : name,
                screen.Bounds.X,
                screen.Bounds.Y,
                screen.Bounds.Width,
                screen.Bounds.Height,
                screen.Scaling,
                screen.IsPrimary);
        }).ToArray();
    }

    public IDisplayOverlayHandle CreateFullScreenOverlay(FeatureDisplay display, Control content, bool topmost = true)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(content);

        var currentDisplay = GetDisplays().FirstOrDefault(candidate =>
            string.Equals(candidate.Id, display.Id, StringComparison.OrdinalIgnoreCase));
        if (currentDisplay is null)
        {
            throw new InvalidOperationException($"Display '{display.Id}' is no longer connected.");
        }

        var scale = currentDisplay.Scaling > 0 ? currentDisplay.Scaling : 1;
        var window = new Window
        {
            Title = $"Ultimate Toolbox overlay · {currentDisplay.Name ?? currentDisplay.Id}",
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowDecorations = WindowDecorations.None,
            ShowInTaskbar = false,
            CanResize = false,
            CanMinimize = false,
            CanMaximize = false,
            Topmost = topmost,
            Background = Brushes.Transparent,
            TransparencyBackgroundFallback = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Position = new PixelPoint(currentDisplay.X, currentDisplay.Y),
            Width = currentDisplay.Width / scale,
            Height = currentDisplay.Height / scale,
            Content = content
        };

        var handle = new OverlayHandle(this, currentDisplay, window);
        try
        {
            var mainWindow = _mainWindowProvider()
                ?? throw new InvalidOperationException("The host window is not available yet.");
            window.Show(mainWindow);
            NativeOverlayInput.SetTransparent(window.TryGetPlatformHandle(), topmost);
            _overlays.Add(handle);
            return handle;
        }
        catch
        {
            window.Close();
            throw;
        }
    }

    public IDisposable HideCursor()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_cursorVisibilityLeaseCount == 0)
        {
            var platformHandle = _mainWindowProvider()?.TryGetPlatformHandle()
                ?? throw new InvalidOperationException("The host window does not have a native display handle yet.");
            _cursorVisibility = NativeCursorVisibility.Acquire(platformHandle);
        }

        _cursorVisibilityLeaseCount++;
        return new CursorVisibilityLease(this);
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_screens is not null)
        {
            _screens.Changed -= OnScreensChanged;
            _screens = null;
        }

        foreach (var overlay in _overlays.ToArray())
        {
            overlay.Dispose();
        }

        RestoreCursorVisibility();

        DisplaysChanged = null;
    }

    private void ReleaseCursorVisibility()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed || _cursorVisibilityLeaseCount == 0)
        {
            return;
        }

        _cursorVisibilityLeaseCount--;
        if (_cursorVisibilityLeaseCount == 0)
        {
            RestoreCursorVisibility();
        }
    }

    private void RestoreCursorVisibility()
    {
        var visibility = _cursorVisibility;
        _cursorVisibility = null;
        _cursorVisibilityLeaseCount = 0;
        visibility?.Dispose();
    }

    private Screens GetScreens()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _mainWindowProvider()?.Screens
            ?? throw new InvalidOperationException("The host window is not available yet.");
    }

    private void TrackScreenChanges(Screens screens)
    {
        if (ReferenceEquals(_screens, screens))
        {
            return;
        }

        if (_screens is not null)
        {
            _screens.Changed -= OnScreensChanged;
        }

        _screens = screens;
        _screens.Changed += OnScreensChanged;
    }

    private void OnScreensChanged(object? sender, EventArgs args) => DisplaysChanged?.Invoke(this, EventArgs.Empty);

    private sealed class OverlayHandle(
        AvaloniaDisplayOverlayService owner,
        FeatureDisplay display,
        Window window) : IDisplayOverlayHandle
    {
        private bool _disposed;

        public FeatureDisplay Display { get; } = display;

        public void SetTopmost(bool topmost)
        {
            Dispatcher.UIThread.VerifyAccess();
            ObjectDisposedException.ThrowIf(_disposed, this);
            window.Topmost = topmost;
            NativeOverlayInput.SetTopmost(window.TryGetPlatformHandle(), topmost);
        }

        public void Dispose()
        {
            Dispatcher.UIThread.VerifyAccess();
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner._overlays.Remove(this);
            window.Close();
        }
    }

    private sealed class CursorVisibilityLease(AvaloniaDisplayOverlayService owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            Dispatcher.UIThread.VerifyAccess();
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner.ReleaseCursorVisibility();
        }
    }
}

internal static class NativeCursorVisibility
{
    public static IDisposable Acquire(IPlatformHandle platformHandle)
    {
        if (OperatingSystem.IsWindows() && platformHandle.HandleDescriptor == "HWND")
        {
            return WindowsCursorVisibility.Acquire();
        }

        if (OperatingSystem.IsLinux() && platformHandle.HandleDescriptor == "X11")
        {
            return X11CursorVisibility.Acquire();
        }

        throw new PlatformNotSupportedException(
            $"Cursor hiding is not available for this display backend ({platformHandle.HandleDescriptor}).");
    }

    private static class WindowsCursorVisibility
    {
        private const int MaximumBalanceCalls = 64;

        public static IDisposable Acquire()
        {
            var calls = 0;
            var displayCount = 0;
            do
            {
                displayCount = ShowCursor(false);
                calls++;
            }
            while (displayCount >= 0 && calls < MaximumBalanceCalls);

            if (displayCount >= 0)
            {
                Restore(calls);
                throw new InvalidOperationException("The Windows pointer display count could not be balanced to a hidden state.");
            }

            return new WindowsCursorLease(calls);
        }

        private static void Restore(int calls)
        {
            for (var index = 0; index < calls; index++)
            {
                ShowCursor(true);
            }
        }

        [DllImport("user32.dll", EntryPoint = "ShowCursor")]
        private static extern int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);

        private sealed class WindowsCursorLease(int balanceCalls) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Restore(balanceCalls);
            }
        }
    }

    private static class X11CursorVisibility
    {
        public static IDisposable Acquire()
        {
            IntPtr display;
            try
            {
                display = XOpenDisplay(IntPtr.Zero);
            }
            catch (DllNotFoundException exception)
            {
                throw new PlatformNotSupportedException("The X11 client libraries are unavailable.", exception);
            }

            if (display == IntPtr.Zero)
            {
                throw new PlatformNotSupportedException("Could not connect to the active X11 display.");
            }

            try
            {
                if (!XFixesQueryExtension(display, out _, out _))
                {
                    throw new PlatformNotSupportedException("The X11 server does not provide the XFixes cursor extension.");
                }

                var rootWindow = XRootWindow(display, XDefaultScreen(display));
                XFixesHideCursor(display, rootWindow);
                XFlush(display);
                return new X11CursorLease(display, rootWindow);
            }
            catch (DllNotFoundException exception)
            {
                XCloseDisplay(display);
                throw new PlatformNotSupportedException("The XFixes library is unavailable.", exception);
            }
            catch
            {
                XCloseDisplay(display);
                throw;
            }
        }

        [DllImport("libX11.so.6", EntryPoint = "XOpenDisplay")]
        private static extern IntPtr XOpenDisplay(IntPtr displayName);

        [DllImport("libX11.so.6", EntryPoint = "XDefaultScreen")]
        private static extern int XDefaultScreen(IntPtr display);

        [DllImport("libX11.so.6", EntryPoint = "XRootWindow")]
        private static extern UIntPtr XRootWindow(IntPtr display, int screenNumber);

        [DllImport("libX11.so.6", EntryPoint = "XFlush")]
        private static extern int XFlush(IntPtr display);

        [DllImport("libX11.so.6", EntryPoint = "XCloseDisplay")]
        private static extern int XCloseDisplay(IntPtr display);

        [DllImport("libXfixes.so.3", EntryPoint = "XFixesQueryExtension")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool XFixesQueryExtension(IntPtr display, out int eventBase, out int errorBase);

        [DllImport("libXfixes.so.3", EntryPoint = "XFixesHideCursor")]
        private static extern void XFixesHideCursor(IntPtr display, UIntPtr window);

        [DllImport("libXfixes.so.3", EntryPoint = "XFixesShowCursor")]
        private static extern void XFixesShowCursor(IntPtr display, UIntPtr window);

        private sealed class X11CursorLease(IntPtr display, UIntPtr rootWindow) : IDisposable
        {
            private IntPtr _display = display;

            public void Dispose()
            {
                if (_display == IntPtr.Zero)
                {
                    return;
                }

                XFixesShowCursor(_display, rootWindow);
                XFlush(_display);
                XCloseDisplay(_display);
                _display = IntPtr.Zero;
            }
        }
    }
}

internal static class NativeOverlayInput
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int SwpNoSize = 0x0001;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoActivate = 0x0010;
    private const int SwpFrameChanged = 0x0020;
    private const long HwndTopmost = -1;
    private const long HwndNoTopmost = -2;
    private const int ShapeInput = 2;
    private const int ShapeSet = 0;
    private const int Unsorted = 0;

    public static void SetTransparent(IPlatformHandle? platformHandle, bool topmost)
    {
        if (platformHandle is null)
        {
            throw new InvalidOperationException("Avalonia did not provide a native overlay window handle.");
        }

        if (OperatingSystem.IsWindows() && platformHandle.HandleDescriptor == "HWND")
        {
            SetWindowsTransparent(platformHandle.Handle, topmost);
            return;
        }

        if (OperatingSystem.IsLinux() && platformHandle.HandleDescriptor == "X11")
        {
            SetX11Transparent(platformHandle.Handle);
            return;
        }

        throw new PlatformNotSupportedException(
            $"Input-transparent overlays are not available for this Linux desktop window backend ({platformHandle.HandleDescriptor}). Use an X11 session or XWayland for screen overlays.");
    }

    private static void SetWindowsTransparent(IntPtr hwnd, bool topmost)
    {
        Marshal.SetLastPInvokeError(0);
        var oldExtendedStyle = GetWindowLongPtr(hwnd, GwlExStyle);
        var lastError = Marshal.GetLastPInvokeError();
        if (oldExtendedStyle == IntPtr.Zero && lastError != 0)
        {
            throw new Win32Exception(lastError, "Could not read the overlay window styles.");
        }

        var currentStyle = oldExtendedStyle.ToInt64();
        var updatedStyle = new IntPtr(currentStyle | WsExTransparent | WsExToolWindow | WsExNoActivate);
        Marshal.SetLastPInvokeError(0);
        var previousStyle = SetWindowLongPtr(hwnd, GwlExStyle, updatedStyle);
        lastError = Marshal.GetLastPInvokeError();
        if (previousStyle == IntPtr.Zero && lastError != 0)
        {
            throw new Win32Exception(lastError, "Could not enable input transparency for the overlay.");
        }

        SetWindowsTopmost(hwnd, topmost);
    }

    public static void SetTopmost(IPlatformHandle? platformHandle, bool topmost)
    {
        if (OperatingSystem.IsWindows() && platformHandle?.HandleDescriptor == "HWND")
        {
            SetWindowsTopmost(platformHandle.Handle, topmost);
        }
    }

    private static void SetWindowsTopmost(IntPtr hwnd, bool topmost)
    {
        var insertAfter = new IntPtr(topmost ? HwndTopmost : HwndNoTopmost);
        if (!SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoActivate | SwpFrameChanged))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not update the overlay window z-order.");
        }
    }

    private static void SetX11Transparent(IntPtr xWindow)
    {
        IntPtr display;
        try
        {
            display = XOpenDisplay(IntPtr.Zero);
        }
        catch (DllNotFoundException exception)
        {
            throw new PlatformNotSupportedException("The X11 client libraries are unavailable.", exception);
        }

        if (display == IntPtr.Zero)
        {
            throw new PlatformNotSupportedException("Could not connect to the active X11 display.");
        }

        try
        {
            if (!XShapeQueryExtension(display, out _, out _) ||
                !XShapeQueryVersion(display, out var major, out var minor) ||
                major < 1 || (major == 1 && minor < 1))
            {
                throw new PlatformNotSupportedException("The X11 server does not provide XShape input regions.");
            }

            var windowId = new UIntPtr(unchecked((ulong)xWindow.ToInt64()));
            XShapeCombineRectangles(display, windowId, ShapeInput, 0, 0, IntPtr.Zero, 0, ShapeSet, Unsorted);
            XFlush(display);
        }
        catch (DllNotFoundException exception)
        {
            throw new PlatformNotSupportedException("The XShape library is unavailable.", exception);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr64(hwnd, index)
        : new IntPtr(GetWindowLong32(hwnd, index));

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) => IntPtr.Size == 8
        ? SetWindowLongPtr64(hwnd, index, value)
        : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, int flags);

    [DllImport("libX11.so.6", EntryPoint = "XOpenDisplay")]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport("libX11.so.6", EntryPoint = "XFlush")]
    private static extern int XFlush(IntPtr display);

    [DllImport("libX11.so.6", EntryPoint = "XCloseDisplay")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libXext.so.6", EntryPoint = "XShapeQueryExtension")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool XShapeQueryExtension(IntPtr display, out int eventBase, out int errorBase);

    [DllImport("libXext.so.6", EntryPoint = "XShapeQueryVersion")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool XShapeQueryVersion(IntPtr display, out int majorVersion, out int minorVersion);

    [DllImport("libXext.so.6", EntryPoint = "XShapeCombineRectangles")]
    private static extern void XShapeCombineRectangles(
        IntPtr display,
        UIntPtr destination,
        int destinationKind,
        int xOffset,
        int yOffset,
        IntPtr rectangles,
        int rectangleCount,
        int operation,
        int ordering);
}
