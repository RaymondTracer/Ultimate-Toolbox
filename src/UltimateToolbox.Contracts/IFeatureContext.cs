namespace UltimateToolbox.Contracts;

public interface IFeatureContext
{
    FeatureMetadata Metadata { get; }

    FeatureOperatingSystem CurrentOperatingSystem { get; }

    string DataDirectory { get; }

    IFeatureSettingsStore Settings { get; }

    IFeatureLogger Logger { get; }

    IDisplayOverlayService DisplayOverlays { get; }
}
