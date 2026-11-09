namespace UltimateToolbox;

internal static class FeaturePaths
{
    internal static string DataDirectory { get; } = ResolveDataDirectory();

    public static string FeatureDirectory { get; } = Path.Combine(DataDirectory, "features");

    public static string SettingsDirectory { get; } = Path.Combine(DataDirectory, "settings");

    private static string ResolveDataDirectory()
    {
        var configuredDataHome = Environment.GetEnvironmentVariable("ULTIMATE_TOOLBOX_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(configuredDataHome))
        {
            if (!Path.IsPathRooted(configuredDataHome))
            {
                throw new InvalidOperationException("ULTIMATE_TOOLBOX_DATA_HOME must be an absolute path.");
            }

            return Path.GetFullPath(configuredDataHome);
        }

        if (OperatingSystem.IsWindows())
        {
            var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localApplicationData, "UltimateToolbox");
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(xdgDataHome) && Path.IsPathRooted(xdgDataHome))
        {
            return Path.Combine(xdgDataHome, "ultimate-toolbox");
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".local", "share", "ultimate-toolbox");
    }
}
