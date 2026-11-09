using System.Text.Json;
using UltimateToolbox.Contracts;

namespace UltimateToolbox;

internal sealed class JsonFeatureSettingsStore : IFeatureSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;
    private readonly object _gate = new();

    public JsonFeatureSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public T? Load<T>() where T : class
    {
        lock (_gate)
        {
            if (!File.Exists(_settingsPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(_settingsPath), JsonOptions);
        }
    }

    public void Save<T>(T settings) where T : class
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_settingsPath)
                ?? throw new InvalidOperationException("The feature settings path has no parent directory.");
            Directory.CreateDirectory(directory);

            var temporaryPath = _settingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
    }
}
