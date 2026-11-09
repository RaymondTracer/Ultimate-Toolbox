# Process Priority

Process Priority is an optional Windows/Linux feature module based on the existing ZandronumPriorityWatcher. The default rule list is empty. Add exact process names in the feature UI; names are case-insensitive and an optional `.exe` suffix is ignored.

Windows rules use `Idle`, `BelowNormal`, `Normal`, `AboveNormal`, or `High` process priority classes. `Realtime` is not offered. Linux rules use nice values from `-20` (highest) to `19` (lowest); the module applies and verifies the setting on each visible thread and retries on the next poll for threads created later. Linux permission failures are reported in the rule status and host log. The module never invokes `sudo` or requests elevation.

The default check interval is two seconds and can be changed from one second to five minutes. Settings are stored through the host's per-feature settings store. A still-installed standalone watcher or Windows IFEO rule may compete with a rule for the same process; the module does not modify or remove those existing settings.

Build the module with:

```powershell
dotnet publish .\features\ProcessPriority\ProcessPriority.Feature\ProcessPriority.Feature.csproj -c Release -o .\artifacts\ProcessPriority
```

Copy the publish output, including `UltimateToolbox.ProcessPriority.tbfeature.dll`, its `.deps.json`, and dependencies, into its own folder under the host's feature directory. Restart Ultimate Toolbox to load it.
