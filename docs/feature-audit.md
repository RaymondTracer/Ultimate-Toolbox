# Feature integration audit

Ultimate Toolbox keeps each optional capability in its own feature module. The host owns discovery, lifecycle, settings storage, and logging. A feature owns its policy and view; Windows and Linux system calls belong behind that feature's platform adapters.

## Requested modules and existing behavior owners

| Feature | Existing owner | Behavior to carry forward | Platform boundary |
| --- | --- | --- | --- |
| Auto Blank Screen | `ActivityMonitor` owns idle/overlay state and audio blocking; `RawInputActivityTracker` owns Windows input tracking; `AudioSessionMonitor` and `CoreAudioSessionEnumerator` own Windows audio-session inspection; `BlankScreenForm` owns multi-display overlays; `Settings` owns current persistence. | Configurable inactivity timeout, process/audio rules, manual blanking, immediate wake when activity resumes, multiple displays, bounded diagnostics. | Replace WinForms timer, Raw Input, Core Audio COM, and overlay forms with host-neutral policy plus Windows/Linux activity, audio, and display providers. Use the feature context for settings and logging. |
| Lock Key Manager | `Form1` owns polling, desired lock-key states, toggles, and presentation; `OverlayForm` owns notifications; Registry methods in `Form1` own settings and sign-in startup. | Caps/Num/Scroll status, manual toggles, optional enforced states, tray/status presentation, notifications. | Separate desired-state policy from key-state and key-injection providers. Registry startup is Windows-only; Linux key-state and injection support depends on the active desktop session. |
| Dimmer | `Monitor.cpp` owns per-display settings, persistence, schedule evaluation, and effective brightness/temperature; `Overlay.cpp` owns gamma and brightness effects; `ScheduleService.cpp` owns location/solar data; `MainWindow.cpp` owns controls and refresh timing. | Manual per-display controls, sunrise/sunset schedule, cached solar data, explicit location resolution, startup option, and independent display schedules. | Keep schedule values shared. Display enumeration and effect application are platform adapters. Avalonia can enumerate displays, but its window API does not supply transparent click-through; transparency also depends on the Linux compositor. Wayland global placement needs compositor-specific verification. |
| Automatic power-plan switching | `Watch-BuildPowerPlan.ps1` owns process classification, a two-second poll, 30-second grace, active-plan capture/restore, and reapplication while activity continues. | Watch exact process names; treat direct `dotnet build/publish/test/restore/msbuild` specially; ignore reusable MSBuild nodes; restore the exact plan that was active before the watched work. | Keep process matching and the busy/idle state machine platform-neutral. Windows uses `powercfg`; Linux needs a separate, capability-checked power-profile adapter. Do not copy the script's machine-specific extra process names into defaults. |
| Generic process priority | `ZandronumPriorityWatcher.Program` owns two-second detection, priority correction/verification, and throttled diagnostics. | Convert the hard-coded `zandronum` rule into user-configurable process-name rules. Never use Realtime. | Windows priority classes and Linux nice values are not equivalent scales. Linux may reject a request for higher priority without suitable privilege/resource limits, and Linux nice is per-thread. Report an applied, denied, or unavailable result per process. |
| Window Manager | `WindowPositionKeeper-ManualPrototype.ps1` owns general save/restore/undo of visible window layout; `ZandWndMan.Program` owns Zandronum-only window decoration removal, placement, and retry state. | Start with general layout save/restore/undo. Keep game-specific window styling separate and opt-in. | The current implementations use Win32 window enumeration and positioning. Linux window control requires a separate desktop-specific adapter; Wayland delegates window management to each compositor. |

## Competing sources of truth and reuse rules

- **Dimmer schedule:** the original `Monitor.cpp` computes effective brightness and temperature from one day/night blend. The rewrite's `Dimmer.Core/SolarSchedule` is the shared owner for both values. Views and effect adapters must consume that result rather than calculate another schedule.
- **Zandronum priority:** the existing Windows IFEO `CpuPriorityClass` registry rule can set initial priority at process creation, while the watcher corrects priority after launch. These have different lifetimes and can compete. The generic feature will not remove or rewrite either existing installation; users can disable their old rule separately if they choose.
- **Window management:** the general layout script and ZandWndMan have overlapping Win32 APIs but different policies. The toolbox should reuse the general layout behavior and keep Zandronum-specific decoration removal as a separate opt-in rule, not silently apply it to every window.
- **Settings and startup:** existing programs own settings in JSON, the executable directory, or Registry. In the toolbox, `IFeatureSettingsStore` owns persisted feature settings. OS startup registration must be an explicit platform service rather than copied into each module's policy.
- **Process priority and power plans:** both inspect processes, but they make independent decisions. A shared process-name matcher can be reused; priority enforcement and power-profile capture/restore remain separate state machines.
- **Dimmer transition timing:** brightness and temperature share one solar blend. Nullable temperature endpoints use neutral 6500 K during the transition and restore the untinted state at the endpoint.

## Additional candidate modules

These are present elsewhere in the user's project collection and could fit the same optional-module model, but are not part of the current requested set:

- **Drive Space Monitor:** useful as a read-only, mostly OS-neutral capacity alert module.
- **Hibernater:** fixed-delay and roulette timer modes exist. Hibernation is an OS service with privilege and availability checks, so this should follow the core requested features.
- **Mouse Mapper and Controller-to-Keyboard Mapper:** broader input/device-management projects with hardware-specific Windows behavior. They need a separate compatibility and device-protocol audit before inclusion.

## Verification and implementation sequence

1. Keep the base app feature-free and load only installed `.tbfeature.dll` modules.
2. Finish the Dimmer feature around the shared schedule core; verify the UI and display adapters on Windows and Linux desktop sessions.
3. Add generic process-priority rules and a cross-platform process matcher; expose Linux permission failures without escalating privileges automatically.
4. Port the automatic power-plan state machine with separate Windows and Linux adapters.
5. Port Auto Blank Screen and Lock Key Manager by extracting policy from their existing Windows input, audio, overlay, Registry, and tray owners.
6. Implement general window layout save/restore with an explicit Linux desktop support matrix; add game-specific styling only as a separate opt-in module.

Avalonia's [screen and window management API](https://docs.avaloniaui.net/docs/app-development/window-management) supports display enumeration and topmost windows, while its [window how-to](https://docs.avaloniaui.net/docs/how-to/window-how-to) documents that transparent click-through is not a built-in cross-platform behavior. The [Wayland protocol model](https://wayland.freedesktop.org/docs/book/Protocol.html) leaves surface placement and global window management with the compositor. Linux priority behavior is documented in [`setpriority(2)`](https://man7.org/linux/man-pages/man2/setpriority.2.html).
