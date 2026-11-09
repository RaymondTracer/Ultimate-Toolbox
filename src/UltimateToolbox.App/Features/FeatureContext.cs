using UltimateToolbox.Contracts;

namespace UltimateToolbox;

internal sealed class FeatureContext : IFeatureContext
{
    public FeatureContext(FeatureMetadata metadata, string dataDirectory, FeatureOperatingSystem currentOperatingSystem)
    {
        Metadata = metadata;
        DataDirectory = dataDirectory;
        CurrentOperatingSystem = currentOperatingSystem;
        Settings = new JsonFeatureSettingsStore(Path.Combine(FeaturePaths.SettingsDirectory, $"{metadata.Id}.json"));
        Logger = new TraceFeatureLogger(metadata.Id);
    }

    public FeatureMetadata Metadata { get; }

    public FeatureOperatingSystem CurrentOperatingSystem { get; }

    public string DataDirectory { get; }

    public IFeatureSettingsStore Settings { get; }

    public IFeatureLogger Logger { get; }
}
