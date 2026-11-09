using Microsoft.Win32;
using UltimateToolbox.Contracts;

namespace UltimateToolbox.Dimmer.Feature;

internal static class UserStartupRegistration
{
    private const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WindowsValueName = "UltimateToolbox.Dimmer";

    public static bool SetEnabled(bool enabled, FeatureOperatingSystem operatingSystem, out string? error)
    {
        error = null;
        try
        {
            if (operatingSystem == FeatureOperatingSystem.Windows)
            {
                return SetWindowsEnabled(enabled, out error);
            }

            if (operatingSystem == FeatureOperatingSystem.Linux)
            {
                return SetLinuxEnabled(enabled, out error);
            }

            error = "Sign-in startup is not available on this operating system.";
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           System.Security.SecurityException or ArgumentException or
                                           PlatformNotSupportedException)
        {
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    private static bool SetWindowsEnabled(bool enabled, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "Windows sign-in startup is not available on this operating system.";
            return false;
        }

        using var key = Registry.CurrentUser.CreateSubKey(WindowsRunKey, writable: true);
        if (key is null)
        {
            error = "Could not open the current user's sign-in settings.";
            return false;
        }

        if (!enabled)
        {
            key.DeleteValue(WindowsValueName, throwOnMissingValue: false);
            return true;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            error = "Could not find the Ultimate Toolbox executable path.";
            return false;
        }

        key.SetValue(WindowsValueName, $"\"{executablePath}\"", RegistryValueKind.String);
        return true;
    }

    private static bool SetLinuxEnabled(bool enabled, out string? error)
    {
        error = null;
        var autostartDirectory = GetLinuxAutostartDirectory();
        var entryPath = Path.Combine(autostartDirectory, "ultimate-toolbox-dimmer.desktop");
        if (!enabled)
        {
            if (File.Exists(entryPath))
            {
                File.Delete(entryPath);
            }

            return true;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathRooted(executablePath))
        {
            error = "Could not find the absolute Ultimate Toolbox executable path.";
            return false;
        }

        Directory.CreateDirectory(autostartDirectory);
        var contents = string.Join(Environment.NewLine,
            "[Desktop Entry]",
            "Type=Application",
            "Name=Ultimate Toolbox Dimmer",
            $"Exec={QuoteDesktopExec(executablePath)}",
            "Terminal=false",
            "X-GNOME-Autostart-enabled=true",
            string.Empty);
        File.WriteAllText(entryPath, contents);
        return true;
    }

    private static string GetLinuxAutostartDirectory()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome) || !Path.IsPathRooted(configHome))
        {
            configHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return Path.Combine(configHome, "autostart");
    }

    private static string QuoteDesktopExec(string executablePath)
    {
        var escaped = executablePath
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
