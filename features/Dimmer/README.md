# Dimmer feature

This feature is a separate Ultimate Toolbox module. The original program at E:\Dimmer is the behavior reference. Its behavior owners are Monitor.cpp (persisted per-display settings and schedule evaluation), Overlay.cpp (display effects), MainWindow.cpp (settings UI and schedule refresh timer), and TrayMenu.cpp (quick controls). The original source is BSD-3-Clause licensed; its notice is retained in THIRD_PARTY_NOTICES.md.

Build Dimmer.Feature and copy its .tbfeature.dll, Dimmer.Core.dll, and dimmer.ico to the feature directory shown by the host. The base application does not bundle this module. Settings live in the host's per-feature data directory. On Windows, the first start imports the existing %APPDATA%\dimmer\config.json profile when no new profile exists; the original file and sign-in entry remain untouched.

Set ULTIMATE_TOOLBOX_DATA_HOME to an absolute path to use a custom data root on Windows or Linux. The host stores the feature modules and settings beneath that directory.

Dimmer.Core owns schedule blending and gamma-ramp color math shared by the display backends. Brightness and temperature use the same smooth solar blend. A scheduled temperature can be set to “No temperature” at either endpoint; the transition fades through neutral 6500 K, and the endpoint restores the original gamma ramp.

Brightness uses a transparent, topmost software overlay. The “Keep the overlay above popup windows” option reasserts its z-order every 250 ms. Windows uses its native display gamma-ramp API for temperature. Linux temperature uses XRandR gamma ramps under X11; a generic per-display gamma API is not available to this module on Wayland. Input-transparent overlays are available on Windows and X11, while native Wayland compositors may reject them. Linux tray availability depends on the desktop environment's StatusNotifierItem or AppIndicator support.

Resolving a city sends the city name to Open-Meteo geocoding. The returned coordinates are cached and refreshed periodically while the schedule is enabled. The module does not request location automatically. Sunrise/sunset data is provided by Open-Meteo: https://open-meteo.com/.
