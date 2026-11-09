# Auto Blank Screen

Auto Blank Screen is an optional Windows/Linux feature module ported from the existing AutoBlankScreen project. It keeps the ten-minute default, 500 ms idle checks, manual blanking, configured audio-app blocking, and automatic wake after input. The shared host `IDisplayOverlayService` owns fullscreen transparent overlays on every connected display and hides the pointer while blanking through a disposable visibility lease.

## Platform behavior

- **Windows:** Raw Input attaches to the host's actual Avalonia HWND. Keyboard input, clicks, and wheel input wake the displays immediately. Pointer movement needs an eight-pixel displacement to ignore the jitter filtered by the original tracker. Core Audio sessions are inspected directly and per-session peak levels are measured.
- **Linux X11:** the feature reads idle time from the X11 `XScreenSaver` extension and output streams from `pactl --format=json list sink-inputs`. The PulseAudio command supports listing sink inputs and JSON output; PipeWire can work through its PulseAudio compatibility service. [pactl manual](https://manpages.debian.org/experimental/pulseaudio-utils/pactl.1.en.html) documents both options.
- **Linux Wayland:** global idle tracking is unavailable in this module, so automatic and manual blanking are disabled for safety. The shared overlay service also reports unsupported native Wayland click-through windows and cursor hiding.

The X11 idle query reports milliseconds since the X server last received input. The Linux provider requires `libX11`, `libXss`, an accessible X11 display, and the XScreenSaver extension. [X.org's Screen Saver extension specification](https://www.x.org/docs/saver/saver.pdf) defines that idle field. If the provider or audio server is unavailable, automatic blanking pauses and the status view reports the cause. Linux stream metadata does not include the per-process peak meter available from Windows Core Audio, so an unmuted, uncorked stream is treated as active.

## Audio rules

The default blocked apps remain `firefox` and `discord`. Rules match case-insensitive substrings of the session process name or application label. Removing every rule switches to “monitor all audio apps.” Recently detected output apps remain available as quick add suggestions, with the list capped at 20 entries.

The original COM enumerator lives inside a Windows-only WinForms executable and is internal to that project, so the cross-platform feature cannot reference it without taking a dependency on the legacy UI project. The feature carries its COM enumerator as a Windows-only adapter and instantiates it only on Windows. The old WinForms application is unchanged.

## Build

```powershell
dotnet publish .\features\AutoBlankScreen\BlankScreen.Feature\BlankScreen.Feature.csproj -c Release -o .\artifacts\AutoBlankScreen
```

Copy the publish output, including `UltimateToolbox.AutoBlankScreen.tbfeature.dll`, its `.deps.json`, and dependencies, into its own folder under the host's feature directory. Restart Ultimate Toolbox to load it.
