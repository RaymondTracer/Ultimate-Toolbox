# Ultimate Toolbox

Ultimate Toolbox is a Windows and Linux desktop host for optional utility features. The base application ships with no utility features; each feature is an independently built .NET module discovered when the application starts.

The desktop shell uses Avalonia so the shared host UI can run on Windows and Linux. Feature rules and settings belong to their feature module. Shared behavior should stay in common code, while OS-native operations live behind platform-specific implementations and are called only when that platform is supported.

## Current state

- The host starts with an empty feature catalog.
- The host loads modules whose assembly filename ends in `.tbfeature.dll`.
- A module declares its name, ID, description, and supported operating systems through `FeatureMetadata`.
- Modules that do not support the current OS are shown as unavailable and are not started.
- Each module gets its own settings store and data directory.
- The host provides shared display enumeration and full-screen input-transparent overlays. The current native backends are Windows and X11; other Linux display backends return an explicit unsupported error.
- A module can keep background work running while its view is closed; the host stops it during shutdown.
- The build workflow compiles the host on both Windows and Linux.
- The [Process Priority module](features/ProcessPriority/README.md) is available separately and starts with an empty process-rule list.
- The [Automatic Power Plan module](features/AutomaticPowerPlan/README.md) is available separately, carries over the existing build watcher rules, and restores the prior profile after activity ends.

## Build and run

Install the .NET 10 SDK, then run:

```powershell
dotnet build UltimateToolbox.sln
dotnet run --project src/UltimateToolbox.App
```

The same commands work from a Linux shell.

## Installing feature modules

The app reads feature modules from the user's data directory:

- Windows: `%LOCALAPPDATA%\UltimateToolbox\features`
- Linux: `$XDG_DATA_HOME/ultimate-toolbox/features`, or `~/.local/share/ultimate-toolbox/features` when `XDG_DATA_HOME` is unset

Copy a feature's `.tbfeature.dll` and its build output dependencies into its own subfolder there, then restart Ultimate Toolbox. Keep the module's `.deps.json` beside its assembly so private module dependencies can be resolved.

## Creating a feature module

Reference `src/UltimateToolbox.Contracts/UltimateToolbox.Contracts.csproj` and implement one public, concrete class with a public parameterless constructor that implements `IToolboxFeature`:

```csharp
public sealed class ExampleFeature : IToolboxFeature
{
    public FeatureMetadata Metadata { get; } = new(
        "example-feature",
        "Example Feature",
        "A short description shown by the host.",
        [FeatureOperatingSystem.Windows, FeatureOperatingSystem.Linux]);

    public Task StartAsync(IFeatureContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Control CreateView(IFeatureContext context) => new TextBlock { Text = "Feature settings" };

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

Set the module's assembly name to end in `.tbfeature`, for example `ExampleFeature.tbfeature`. Its `Metadata.Id` must be unique and contain lowercase letters, digits, dots, underscores, or hyphens. The host rejects malformed or duplicate IDs and isolates load errors so one broken module does not prevent other modules from loading.

Use shared feature logic for rules and state transitions. Put Windows or Linux calls in platform-specific services within the feature and check support before invoking them. Features can advertise both operating systems while selecting the matching implementation at runtime. Linux desktop capabilities can vary by display server and distribution, so a module should report unavailable capabilities when it cannot safely provide them.

Feature assemblies run in the host process with the user's permissions. Only install modules you trust; .NET assembly loading does not sandbox plugin code.

## Feature modules in progress

- Auto Blank Screen rewrite
- Lock Key Manager
- Further Dimmer UI and platform verification
- Window layout management

Remaining utilities stay optional and will be added as separately loadable modules, with OS support declared per feature.
