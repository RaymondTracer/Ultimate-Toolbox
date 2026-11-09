using UltimateToolbox.PowerPlan;

True("compiler tools are watched by default", WatchedProcessPolicy.Matches("clang-cl", "CLANG-CL.EXE"));
False("user-specific Plex rule is not defaulted", WatchedProcessPolicy.DefaultWatchedProcessNames.Contains("Plex HTPC"));
False("generic dotnet.exe is not an exact watched-name default", WatchedProcessPolicy.DefaultWatchedProcessNames.Contains("dotnet"));

SequenceEqual(
    "watched names normalize extensions, duplicates, and generic dotnet",
    ["clang", "game client"],
    WatchedProcessPolicy.NormalizeProcessNames(["clang.exe", "CLANG", "dotnet", "game client"]));
SequenceEqual(
    "dotnet verbs accept only the supported direct CLI commands",
    ["build", "msbuild"],
    WatchedProcessPolicy.NormalizeDotNetVerbs(["BUILD", "run", "msbuild"]));

Equal(
    "quoted Windows dotnet build invocation is detected",
    "build",
    WatchedProcessPolicy.GetWatchedDotNetVerb("\"C:\\Program Files\\dotnet\\dotnet.exe\" build App.sln"));
Equal(
    "plain dotnet restore invocation is detected",
    "restore",
    WatchedProcessPolicy.GetWatchedDotNetVerb("dotnet restore App.sln"));
Equal(
    "quoted Linux dotnet publish invocation is detected",
    "publish",
    WatchedProcessPolicy.GetWatchedDotNetVerb("\"/usr/share/dotnet/dotnet\" \"publish\" App.csproj"));
Equal(
    "PATH-resolved Linux dotnet build invocation is detected",
    "build",
    WatchedProcessPolicy.GetWatchedDotNetVerb("\"dotnet\" \"build\" App.csproj"));
Equal(
    "generic dotnet run is ignored even when a later argument contains build",
    null,
    WatchedProcessPolicy.GetWatchedDotNetVerb("dotnet run --configuration build"));
Equal(
    "a path containing build does not make a generic dotnet invocation watched",
    null,
    WatchedProcessPolicy.GetWatchedDotNetVerb("dotnet /sdk/build/host.dll"));
True("MSBuild /nodemode worker is identified", WatchedProcessPolicy.IsReusableMsBuildWorker("MSBuild.exe /nologo /nodemode:1 /nodeReuse:true"));
True("MSBuild -nodemode worker is identified", WatchedProcessPolicy.IsReusableMsBuildWorker("MSBuild.dll -nodemode 1"));
False("top-level MSBuild command is not mistaken for a worker", WatchedProcessPolicy.IsReusableMsBuildWorker("MSBuild.exe /restore App.sln"));

var start = new DateTimeOffset(2026, 11, 10, 1, 0, 0, TimeSpan.Zero);
var idle = new PowerPlanRuntimeState();
var activated = PowerPlanStateMachine.Evaluate(
    idle,
    watchedActivityActive: true,
    currentProfileId: "balanced",
    targetProfileId: "performance",
    now: start,
    gracePeriod: TimeSpan.FromSeconds(30));
Equal("first activity captures and selects the target", PowerPlanAction.Activated, activated.Action);
Equal("the exact previously active profile is saved", "balanced", activated.NextState.RestoreProfileId);
Equal("the target profile is requested", "performance", activated.RequestedProfileId);

var graceStarted = PowerPlanStateMachine.Evaluate(
    activated.NextState,
    watchedActivityActive: false,
    currentProfileId: "performance",
    targetProfileId: "performance",
    now: start.AddSeconds(10),
    gracePeriod: TimeSpan.FromSeconds(30));
Equal("idle activity starts the restore grace", PowerPlanAction.GraceStarted, graceStarted.Action);

var waiting = PowerPlanStateMachine.Evaluate(
    graceStarted.NextState,
    watchedActivityActive: false,
    currentProfileId: "performance",
    targetProfileId: "performance",
    now: start.AddSeconds(25),
    gracePeriod: TimeSpan.FromSeconds(30));
Equal("profile remains selected before grace expiry", PowerPlanAction.WaitingForGrace, waiting.Action);

var resumed = PowerPlanStateMachine.Evaluate(
    waiting.NextState,
    watchedActivityActive: true,
    currentProfileId: "performance",
    targetProfileId: "performance",
    now: start.AddSeconds(26),
    gracePeriod: TimeSpan.FromSeconds(30));
Equal("activity during grace cancels restoration", PowerPlanAction.ActivityResumed, resumed.Action);

var reapply = PowerPlanStateMachine.Evaluate(
    resumed.NextState,
    watchedActivityActive: true,
    currentProfileId: "balanced",
    targetProfileId: "performance",
    now: start.AddSeconds(27),
    gracePeriod: TimeSpan.FromSeconds(30));
Equal("a competing profile change is corrected", PowerPlanAction.Reapplied, reapply.Action);

var restored = PowerPlanStateMachine.Evaluate(
    reapply.NextState,
    watchedActivityActive: false,
    currentProfileId: "performance",
    targetProfileId: "performance",
    now: start.AddSeconds(60),
    gracePeriod: TimeSpan.FromSeconds(30));
Equal("elapsed grace restores the original profile", PowerPlanAction.Restored, restored.Action);
Equal("restoration uses the saved profile ID", "balanced", restored.RequestedProfileId);
False("restoration clears switch state", restored.NextState.SwitchInProgress);

var zeroGrace = PowerPlanStateMachine.Evaluate(
    activated.NextState,
    watchedActivityActive: false,
    currentProfileId: "performance",
    targetProfileId: null,
    now: start.AddSeconds(1),
    gracePeriod: TimeSpan.Zero);
Equal("restoration still works when a target profile disappears", PowerPlanAction.Restored, zeroGrace.Action);

var unavailableBaseline = PowerPlanStateMachine.Evaluate(
    idle,
    watchedActivityActive: true,
    currentProfileId: null,
    targetProfileId: "performance",
    now: start,
    gracePeriod: TimeSpan.FromSeconds(30));
Equal("missing current profile prevents a new switch", PowerPlanAction.BaselineUnavailable, unavailableBaseline.Action);

var restoreNow = PowerPlanStateMachine.RestoreNow(activated.NextState);
Equal("pause or shutdown restores immediately", PowerPlanAction.Restored, restoreNow.Action);
Equal("immediate restore keeps the saved profile", "balanced", restoreNow.RequestedProfileId);

Console.WriteLine("Automatic Power Plan regression checks passed.");
return;

static void Equal<T>(string name, T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}.");
    }
}

static void True(string name, bool actual)
{
    if (!actual)
    {
        throw new InvalidOperationException($"{name}: expected true.");
    }
}

static void False(string name, bool actual)
{
    if (actual)
    {
        throw new InvalidOperationException($"{name}: expected false.");
    }
}

static void SequenceEqual<T>(string name, IReadOnlyList<T> expected, IReadOnlyList<T> actual)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException($"{name}: sequences did not match.");
    }
}
