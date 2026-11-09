using System.Text.Json;
using UltimateToolbox.Contracts;
using UltimateToolbox.Dimmer;

namespace UltimateToolbox.Dimmer.Feature;

internal static class LegacyDimmerConfigImporter
{
    public static DimmerSettings? TryImport(string path, IReadOnlyList<FeatureDisplay> connectedDisplays)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var settings = new DimmerSettings();

            if (TryObject(root, "general", out var general))
            {
                settings.Enabled = GetBoolean(general, "globalEnabled", true);
                settings.KeepOverlayAbovePopups = GetBoolean(general, "pollingEnabled", false);
            }

            if (TryObject(root, "schedule", out var schedule))
            {
                settings.Schedule.Enabled = GetBoolean(schedule, "enabled", false);
                settings.Schedule.City = GetString(schedule, "city");
                settings.Schedule.ResolvedLocation = GetString(schedule, "resolvedLocation");
                settings.Schedule.TimeZone = GetString(schedule, "timeZone");
                settings.Schedule.LocationResolved = GetBoolean(schedule, "locationResolved", false);
                settings.Schedule.Latitude = GetDouble(schedule, "latitude");
                settings.Schedule.Longitude = GetDouble(schedule, "longitude");
                settings.Schedule.TransitionMinutes = GetInt32(schedule, "transitionMinutes", 60);
                settings.Schedule.FetchedAtUtc = GetInt64(schedule, "fetchedAtUtc");
                settings.Schedule.SolarDays = ReadSolarDays(schedule);
            }

            if (TryObject(root, "monitors", out var monitors))
            {
                foreach (var legacyMonitor in monitors.EnumerateObject())
                {
                    var display = FindDisplay(legacyMonitor.Name, connectedDisplays);
                    if (display is null || legacyMonitor.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var monitor = legacyMonitor.Value;
                    var imported = new DisplayDimmerSettings
                    {
                        DisplayId = display.Id,
                        Enabled = GetBoolean(monitor, "enabled", true),
                        Brightness = (int)Math.Round(
                            100 - Math.Clamp(GetSingle(monitor, "opacity", 0.3f), 0, 1) * 100,
                            MidpointRounding.AwayFromZero),
                        TemperatureKelvin = ReadTemperature(monitor, "temperature", -1)
                    };

                    if (TryObject(monitor, "schedule", out var monitorSchedule))
                    {
                        imported.ScheduleEnabled = GetBoolean(monitorSchedule, "enabled", false);
                        imported.DayBrightness = GetInt32(monitorSchedule, "dayBrightness", 70);
                        imported.NightBrightness = GetInt32(monitorSchedule, "nightBrightness", 45);
                        imported.DayTemperatureKelvin = ReadTemperature(monitorSchedule, "dayTemperature", 6000);
                        imported.NightTemperatureKelvin = ReadTemperature(monitorSchedule, "nightTemperature", 4500);
                    }

                    settings.Displays.Add(imported);
                }
            }

            settings.Normalize();
            return settings;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FeatureDisplay? FindDisplay(string legacyId, IReadOnlyList<FeatureDisplay> displays)
    {
        foreach (var display in displays)
        {
            if (string.Equals(legacyId, display.Id, StringComparison.OrdinalIgnoreCase))
            {
                return display;
            }

            if (!string.IsNullOrWhiteSpace(display.Name) &&
                legacyId.StartsWith(display.Name + "-", StringComparison.OrdinalIgnoreCase))
            {
                return display;
            }
        }

        return null;
    }

    private static List<SolarDay> ReadSolarDays(JsonElement schedule)
    {
        if (!schedule.TryGetProperty("sunTimes", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<SolarDay>();
        foreach (var value in values.EnumerateArray())
        {
            var sunrise = GetInt64(value, "sunriseUtc");
            var sunset = GetInt64(value, "sunsetUtc");
            if (sunrise <= 0 || sunset <= sunrise)
            {
                continue;
            }

            try
            {
                result.Add(new SolarDay(
                    DateTimeOffset.FromUnixTimeSeconds(sunrise),
                    DateTimeOffset.FromUnixTimeSeconds(sunset)));
            }
            catch (ArgumentOutOfRangeException)
            {
                // Ignore malformed or out-of-range cached events.
            }
        }

        return result;
    }

    private static int? ReadTemperature(JsonElement value, string propertyName, int fallback)
    {
        var temperature = GetInt32(value, propertyName, fallback);
        return temperature < 0 ? null : temperature;
    }

    private static bool TryObject(JsonElement parent, string propertyName, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(propertyName, out value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string GetString(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static bool GetBoolean(JsonElement value, string propertyName, bool fallback) =>
        value.TryGetProperty(propertyName, out var property) &&
        (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? property.GetBoolean()
            : fallback;

    private static int GetInt32(JsonElement value, string propertyName, int fallback) =>
        value.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var result)
            ? result
            : fallback;

    private static long GetInt64(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var result)
            ? result
            : 0;

    private static double GetDouble(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var result)
            ? result
            : 0;

    private static float GetSingle(JsonElement value, string propertyName, float fallback) =>
        value.TryGetProperty(propertyName, out var property) && property.TryGetSingle(out var result)
            ? result
            : fallback;
}
