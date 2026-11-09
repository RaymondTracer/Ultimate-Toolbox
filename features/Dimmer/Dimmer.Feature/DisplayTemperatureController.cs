using System.ComponentModel;
using System.Runtime.InteropServices;
using UltimateToolbox.Contracts;
using UltimateToolbox.Dimmer;

namespace UltimateToolbox.Dimmer.Feature;

internal interface IDisplayTemperatureBackend : IDisposable
{
    void Apply(int? temperatureKelvin);
}

internal static class DisplayTemperatureController
{
    public static IDisplayTemperatureBackend Create(FeatureDisplay display, FeatureOperatingSystem operatingSystem)
    {
        if (string.IsNullOrWhiteSpace(display.Name))
        {
            throw new PlatformNotSupportedException("This display has no system name for color-temperature control.");
        }

        if (operatingSystem == FeatureOperatingSystem.Windows)
        {
            return new WindowsGammaBackend(display.Name);
        }

        if (operatingSystem == FeatureOperatingSystem.Linux)
        {
            return new XrandrGammaBackend(display.Name);
        }

        throw new PlatformNotSupportedException("Color-temperature control is not available on this operating system.");
    }
}

internal sealed class WindowsGammaBackend : IDisplayTemperatureBackend
{
    private const int RampLength = 256;
    private readonly string _displayName;
    private IntPtr _deviceContext;
    private ushort[]? _originalRamp;
    private int? _lastTemperature;

    public WindowsGammaBackend(string displayName)
    {
        _displayName = displayName;
    }

    public void Apply(int? temperatureKelvin)
    {
        if (temperatureKelvin is null)
        {
            Restore();
            return;
        }

        EnsureOriginalRamp();
        if (_lastTemperature == temperatureKelvin)
        {
            return;
        }

        var factors = ColorTemperatureMath.ToRgbFactors(temperatureKelvin.Value);
        var ramp = ColorTemperatureMath.ApplyToRgbRamp(_originalRamp!, RampLength, factors);
        if (!SetDeviceGammaRamp(_deviceContext, ramp))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not set the color temperature for '{_displayName}'.");
        }

        _lastTemperature = temperatureKelvin;
    }

    public void Dispose()
    {
        try
        {
            Restore();
        }
        finally
        {
            if (_deviceContext != IntPtr.Zero)
            {
                DeleteDC(_deviceContext);
                _deviceContext = IntPtr.Zero;
            }
        }
    }

    private void EnsureOriginalRamp()
    {
        if (_originalRamp is not null)
        {
            return;
        }

        _deviceContext = CreateDCW(null, _displayName, null, IntPtr.Zero);
        if (_deviceContext == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open display '{_displayName}' for color-temperature control.");
        }

        var original = new ushort[RampLength * 3];
        if (!GetDeviceGammaRamp(_deviceContext, original))
        {
            var errorCode = Marshal.GetLastWin32Error();
            DeleteDC(_deviceContext);
            _deviceContext = IntPtr.Zero;
            throw new Win32Exception(errorCode, $"This display does not expose a color-temperature ramp ('{_displayName}').");
        }

        _originalRamp = original;
    }

    private void Restore()
    {
        if (_deviceContext != IntPtr.Zero && _originalRamp is not null && _lastTemperature is not null)
        {
            if (!SetDeviceGammaRamp(_deviceContext, _originalRamp))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not restore the original display ramp for '{_displayName}'.");
            }
        }

        _lastTemperature = null;
    }

    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDCW(string? driver, string device, string? output, IntPtr initData);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDeviceGammaRamp(IntPtr deviceContext, [Out] ushort[] ramp);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDeviceGammaRamp(IntPtr deviceContext, [In] ushort[] ramp);
}

internal sealed class XrandrGammaBackend : IDisplayTemperatureBackend
{
    private readonly string _outputName;
    private IntPtr _display;
    private UIntPtr _crtc;
    private IntPtr _resources;
    private ushort[]? _originalRed;
    private ushort[]? _originalGreen;
    private ushort[]? _originalBlue;
    private int? _lastTemperature;

    public XrandrGammaBackend(string outputName)
    {
        _outputName = outputName;
        try
        {
            Initialize();
        }
        catch
        {
            ReleaseNativeResources();
            throw;
        }
    }

    public void Apply(int? temperatureKelvin)
    {
        if (temperatureKelvin is null)
        {
            Restore();
            return;
        }

        if (_lastTemperature == temperatureKelvin)
        {
            return;
        }

        var factors = ColorTemperatureMath.ToRgbFactors(temperatureKelvin.Value);
        SetRamp(
            ColorTemperatureMath.ScaleRamp(_originalRed!, factors.Red),
            ColorTemperatureMath.ScaleRamp(_originalGreen!, factors.Green),
            ColorTemperatureMath.ScaleRamp(_originalBlue!, factors.Blue));
        _lastTemperature = temperatureKelvin;
    }

    public void Dispose()
    {
        try
        {
            Restore();
        }
        finally
        {
            ReleaseNativeResources();
        }
    }

    private void ReleaseNativeResources()
    {
        if (_resources != IntPtr.Zero)
        {
            XRRFreeScreenResources(_resources);
            _resources = IntPtr.Zero;
        }

        if (_display != IntPtr.Zero)
        {
            XCloseDisplay(_display);
            _display = IntPtr.Zero;
        }
    }

    private void Initialize()
    {
        try
        {
            _display = XOpenDisplay(IntPtr.Zero);
        }
        catch (DllNotFoundException exception)
        {
            throw new PlatformNotSupportedException("X11 client libraries are unavailable for color-temperature control.", exception);
        }

        if (_display == IntPtr.Zero)
        {
            throw new PlatformNotSupportedException("Could not connect to the X11 display for color-temperature control.");
        }

        var root = XRootWindow(_display, XDefaultScreen(_display));
        _resources = XRRGetScreenResourcesCurrent(_display, root);
        if (_resources == IntPtr.Zero)
        {
            throw new PlatformNotSupportedException("The active X11 server does not expose RandR display resources.");
        }

        var resources = Marshal.PtrToStructure<XrandrScreenResources>(_resources);
        var foundOutput = false;
        for (var index = 0; index < resources.OutputCount; index++)
        {
            var output = ReadXid(resources.Outputs, index);
            var outputInfoPointer = XRRGetOutputInfo(_display, _resources, output);
            if (outputInfoPointer == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                var outputInfo = Marshal.PtrToStructure<XrandrOutputInfo>(outputInfoPointer);
                var name = Marshal.PtrToStringAnsi(outputInfo.Name, outputInfo.NameLength);
                if (!string.Equals(name, _outputName, StringComparison.OrdinalIgnoreCase) || outputInfo.Crtc == UIntPtr.Zero)
                {
                    continue;
                }

                _crtc = outputInfo.Crtc;
                foundOutput = true;
                break;
            }
            finally
            {
                XRRFreeOutputInfo(outputInfoPointer);
            }
        }

        if (!foundOutput)
        {
            throw new PlatformNotSupportedException($"No active XRandR output matches '{_outputName}'.");
        }

        var gammaPointer = XRRGetCrtcGamma(_display, _crtc);
        if (gammaPointer == IntPtr.Zero)
        {
            throw new PlatformNotSupportedException($"XRandR did not expose a gamma ramp for '{_outputName}'.");
        }

        try
        {
            var gamma = Marshal.PtrToStructure<XrandrCrtcGamma>(gammaPointer);
            if (gamma.Size <= 0 || gamma.Red == IntPtr.Zero || gamma.Green == IntPtr.Zero || gamma.Blue == IntPtr.Zero)
            {
                throw new PlatformNotSupportedException($"XRandR returned an empty gamma ramp for '{_outputName}'.");
            }

            _originalRed = ReadRamp(gamma.Red, gamma.Size);
            _originalGreen = ReadRamp(gamma.Green, gamma.Size);
            _originalBlue = ReadRamp(gamma.Blue, gamma.Size);
        }
        finally
        {
            XRRFreeGamma(gammaPointer);
        }
    }

    private void Restore()
    {
        if (_display != IntPtr.Zero && _originalRed is not null && _lastTemperature is not null)
        {
            SetRamp(_originalRed, _originalGreen!, _originalBlue!);
        }

        _lastTemperature = null;
    }

    private void SetRamp(ushort[] red, ushort[] green, ushort[] blue)
    {
        var redPin = GCHandle.Alloc(red, GCHandleType.Pinned);
        var greenPin = GCHandle.Alloc(green, GCHandleType.Pinned);
        var bluePin = GCHandle.Alloc(blue, GCHandleType.Pinned);
        try
        {
            var gamma = new XrandrCrtcGamma
            {
                Size = red.Length,
                Red = redPin.AddrOfPinnedObject(),
                Green = greenPin.AddrOfPinnedObject(),
                Blue = bluePin.AddrOfPinnedObject()
            };
            XRRSetCrtcGamma(_display, _crtc, ref gamma);
            XFlush(_display);
        }
        finally
        {
            redPin.Free();
            greenPin.Free();
            bluePin.Free();
        }
    }

    private static UIntPtr ReadXid(IntPtr values, int index)
    {
        var value = Marshal.ReadIntPtr(values, index * IntPtr.Size).ToInt64();
        return new UIntPtr(unchecked((ulong)value));
    }

    private static ushort[] ReadRamp(IntPtr values, int size)
    {
        var ramp = new ushort[size];
        for (var index = 0; index < size; index++)
        {
            ramp[index] = unchecked((ushort)Marshal.ReadInt16(values, index * sizeof(ushort)));
        }

        return ramp;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XrandrScreenResources
    {
        public UIntPtr Timestamp;
        public UIntPtr ConfigTimestamp;
        public int CrtcCount;
        public IntPtr Crtcs;
        public int OutputCount;
        public IntPtr Outputs;
        public int ModeCount;
        public IntPtr Modes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XrandrOutputInfo
    {
        public UIntPtr Timestamp;
        public UIntPtr Crtc;
        public IntPtr Name;
        public int NameLength;
        public UIntPtr MillimeterWidth;
        public UIntPtr MillimeterHeight;
        public int Connection;
        public int SubpixelOrder;
        public int CrtcCount;
        public IntPtr Crtcs;
        public int CloneCount;
        public IntPtr Clones;
        public int ModeCount;
        public int PreferredModeCount;
        public IntPtr Modes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XrandrCrtcGamma
    {
        public int Size;
        public IntPtr Red;
        public IntPtr Green;
        public IntPtr Blue;
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

    [DllImport("libXrandr.so.2", EntryPoint = "XRRGetScreenResourcesCurrent")]
    private static extern IntPtr XRRGetScreenResourcesCurrent(IntPtr display, UIntPtr window);

    [DllImport("libXrandr.so.2", EntryPoint = "XRRFreeScreenResources")]
    private static extern void XRRFreeScreenResources(IntPtr resources);

    [DllImport("libXrandr.so.2", EntryPoint = "XRRGetOutputInfo")]
    private static extern IntPtr XRRGetOutputInfo(IntPtr display, IntPtr resources, UIntPtr output);

    [DllImport("libXrandr.so.2", EntryPoint = "XRRFreeOutputInfo")]
    private static extern void XRRFreeOutputInfo(IntPtr outputInfo);

    [DllImport("libXrandr.so.2", EntryPoint = "XRRGetCrtcGamma")]
    private static extern IntPtr XRRGetCrtcGamma(IntPtr display, UIntPtr crtc);

    [DllImport("libXrandr.so.2", EntryPoint = "XRRFreeGamma")]
    private static extern void XRRFreeGamma(IntPtr gamma);

    [DllImport("libXrandr.so.2", EntryPoint = "XRRSetCrtcGamma")]
    private static extern void XRRSetCrtcGamma(IntPtr display, UIntPtr crtc, ref XrandrCrtcGamma gamma);
}
