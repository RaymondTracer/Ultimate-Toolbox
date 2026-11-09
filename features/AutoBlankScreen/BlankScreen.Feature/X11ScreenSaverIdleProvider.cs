using System.Runtime.InteropServices;

namespace UltimateToolbox.AutoBlankScreen.Feature;

/// <summary>Queries X11's screen-saver extension for server-wide idle time.</summary>
internal sealed class X11ScreenSaverIdleProvider : IIdleTimeProvider
{
    private IntPtr _display;
    private IntPtr _information;
    private readonly UIntPtr _rootWindow;
    private bool _disposed;

    public X11ScreenSaverIdleProvider()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
                "wayland",
                StringComparison.OrdinalIgnoreCase))
        {
            Error = "Global idle monitoring is not available in a Wayland session; use an X11 session for Auto Blank Screen.";
            Description = "Unavailable Wayland idle source";
            return;
        }

        try
        {
            _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero)
            {
                Error = "No X11 display is available for idle monitoring.";
                Description = "Unavailable X11 idle source";
                return;
            }

            _rootWindow = XRootWindow(_display, XDefaultScreen(_display));
            _information = XScreenSaverAllocInfo();
            if (_information == IntPtr.Zero || XScreenSaverQueryInfo(_display, _rootWindow, _information) == 0)
            {
                Error = "The current X11 server does not provide the XScreenSaver idle-time extension.";
                Description = "Unavailable X11 idle source";
                ReleaseNativeResources();
                return;
            }

            Description = "X11 XScreenSaver idle-time query";
        }
        catch (DllNotFoundException exception)
        {
            Error = $"The X11 screen-saver libraries are unavailable: {exception.Message}";
            Description = "Unavailable X11 idle source";
            ReleaseNativeResources();
        }
        catch (EntryPointNotFoundException exception)
        {
            Error = $"The X11 screen-saver API is unavailable: {exception.Message}";
            Description = "Unavailable X11 idle source";
            ReleaseNativeResources();
        }
    }

    public string Description { get; }

    public string? Error { get; }

    public TimeSpan? GetIdleTime()
    {
        if (_disposed || _display == IntPtr.Zero || _information == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (XScreenSaverQueryInfo(_display, _rootWindow, _information) == 0)
            {
                return null;
            }

            var result = Marshal.PtrToStructure<XScreenSaverInformation>(_information);
            return TimeSpan.FromMilliseconds(result.IdleMilliseconds.ToUInt64());
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseNativeResources();
    }

    private void ReleaseNativeResources()
    {
        if (_information != IntPtr.Zero)
        {
            XFree(_information);
            _information = IntPtr.Zero;
        }

        if (_display != IntPtr.Zero)
        {
            XCloseDisplay(_display);
            _display = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XScreenSaverInformation
    {
        public UIntPtr Window;
        public int State;
        public int Kind;
        public UIntPtr TimeSinceOrUntil;
        public UIntPtr IdleMilliseconds;
        public UIntPtr EventMask;
    }

    [DllImport("libX11.so.6", EntryPoint = "XOpenDisplay")]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport("libX11.so.6", EntryPoint = "XDefaultScreen")]
    private static extern int XDefaultScreen(IntPtr display);

    [DllImport("libX11.so.6", EntryPoint = "XRootWindow")]
    private static extern UIntPtr XRootWindow(IntPtr display, int screenNumber);

    [DllImport("libX11.so.6", EntryPoint = "XCloseDisplay")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6", EntryPoint = "XFree")]
    private static extern int XFree(IntPtr data);

    [DllImport("libXss.so.1", EntryPoint = "XScreenSaverAllocInfo")]
    private static extern IntPtr XScreenSaverAllocInfo();

    [DllImport("libXss.so.1", EntryPoint = "XScreenSaverQueryInfo")]
    private static extern int XScreenSaverQueryInfo(IntPtr display, UIntPtr drawable, IntPtr info);
}
