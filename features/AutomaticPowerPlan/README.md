# Automatic Power Plan

Automatic Power Plan is an optional Windows/Linux feature based on `C:\Users\user\Documents\Watch-BuildPowerPlan.ps1`. It preserves the two-second polling interval, 30-second grace period, exact process-name matching, the direct `dotnet build/publish/test/restore/msbuild` filter, and the MSBuild `/nodemode` worker exception.

The default watched names cover common C# and C++ build tools. The standalone script's machine-specific app names are left out; add any of those in the feature UI. Generic `dotnet.exe`, `dotnet run`, open IDEs, Codex, and Roslyn's shared compiler are not watched by default. The `dotnet` verb list and reusable MSBuild worker behavior can be changed in the UI.

Windows uses `powercfg` to list plans, resolve `SCHEME_MIN`, read the active plan, and switch by exact scheme GUID. Linux uses `powerprofilesctl` when an available power-profiles service provides it; the feature reads the available profile list and uses `performance` as the suggested target when present. Other Linux power-management stacks are reported unavailable until an adapter is added.

Before switching, the module saves the profile that was active. It restores that exact profile after the grace period, when switching is paused, and when the host stops. It reapplies the temporary target if another program changes profiles while watched activity continues. The runtime state is saved so an interrupted host can recover a switch it previously made.

Build the module with:

```powershell
dotnet publish .\features\AutomaticPowerPlan\PowerPlan.Feature\PowerPlan.Feature.csproj -c Release -o .\artifacts\AutomaticPowerPlan
```

Copy the publish output, including `UltimateToolbox.PowerPlan.tbfeature.dll`, its `.deps.json`, and dependencies, into its own folder under the host's feature directory. Restart Ultimate Toolbox to load it.
