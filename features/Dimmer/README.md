# Dimmer feature

This feature is being rewritten for the Ultimate Toolbox plugin host. The original program at `E:\Dimmer` is the behavior reference. Its current owners are `Monitor.cpp` (persisted per-display settings and schedule evaluation), `Overlay.cpp` (display effects), and `MainWindow.cpp` (settings UI and schedule refresh timer). The original source is BSD-3-Clause licensed; its notice is retained in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

The first rewrite slice extracts the shared solar blend and scheduled-value calculations into `Dimmer.Core`. Both brightness and temperature will use this single blend calculation. Scheduled temperature endpoints are nullable: `null` means neutral/no tint, and the transition interpolates from neutral 6500 K to or from the selected tint.

The plugin and display-effect backends will be added in later slices. The core library is not a loadable feature module and is not included in the base application's runtime.
