using UltimateToolbox.Dimmer;

namespace UltimateToolbox.Dimmer.Feature;

public sealed class DimmerSettings
{
    public bool Enabled { get; set; } = true;

    public bool KeepOverlayAbovePopups { get; set; }

    public bool StartOnSignIn { get; set; }

    public DimmerScheduleSettings Schedule { get; set; } = new();

    public List<DisplayDimmerSettings> Displays { get; set; } = [];

    public void Normalize()
    {
        Schedule ??= new DimmerScheduleSettings();
        Displays ??= [];
        Schedule.TransitionMinutes = Math.Clamp(Schedule.TransitionMinutes, 1, 240);
        Schedule.SolarDays ??= [];
        Schedule.SolarDays = Schedule.SolarDays
            .Where(day => day is not null && day.Sunset > day.Sunrise)
            .OrderBy(day => day.Sunrise)
            .ToList();

        Schedule.LocationResolved = Schedule.LocationResolved &&
            double.IsFinite(Schedule.Latitude) && Math.Abs(Schedule.Latitude) <= 90 &&
            double.IsFinite(Schedule.Longitude) && Math.Abs(Schedule.Longitude) <= 180;
        if (Schedule.FetchedAtUtc is < 0 or > 253402300799)
        {
            Schedule.FetchedAtUtc = 0;
        }

        Displays = Displays.Where(display => display is not null).ToList();
        foreach (var display in Displays)
        {
            display.Brightness = Math.Clamp(display.Brightness, 0, 100);
            display.TemperatureKelvin = NormalizeTemperature(display.TemperatureKelvin);
            display.DayBrightness = Math.Clamp(display.DayBrightness, 0, 100);
            display.NightBrightness = Math.Clamp(display.NightBrightness, 0, 100);
            display.DayTemperatureKelvin = NormalizeTemperature(display.DayTemperatureKelvin);
            display.NightTemperatureKelvin = NormalizeTemperature(display.NightTemperatureKelvin);
        }

        var uniqueDisplays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Displays = Displays
            .Where(display => !string.IsNullOrWhiteSpace(display.DisplayId) && uniqueDisplays.Add(display.DisplayId))
            .ToList();
    }

    public DisplayDimmerSettings GetOrAddDisplay(string displayId)
    {
        var existing = Displays.FirstOrDefault(display =>
            string.Equals(display.DisplayId, displayId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var created = new DisplayDimmerSettings { DisplayId = displayId };
        Displays.Add(created);
        return created;
    }

    private static int? NormalizeTemperature(int? temperature) => temperature is null
        ? null
        : Math.Clamp(temperature.Value, 4500, 6000);
}

public sealed class DimmerScheduleSettings
{
    public bool Enabled { get; set; }

    public string City { get; set; } = string.Empty;

    public string ResolvedLocation { get; set; } = string.Empty;

    public string TimeZone { get; set; } = string.Empty;

    public bool LocationResolved { get; set; }

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public int TransitionMinutes { get; set; } = 60;

    public long FetchedAtUtc { get; set; }

    public List<SolarDay> SolarDays { get; set; } = [];
}

public sealed class DisplayDimmerSettings
{
    public string DisplayId { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public int Brightness { get; set; } = 70;

    public int? TemperatureKelvin { get; set; }

    public bool ScheduleEnabled { get; set; }

    public int DayBrightness { get; set; } = 70;

    public int NightBrightness { get; set; } = 45;

    public int? DayTemperatureKelvin { get; set; } = 6000;

    public int? NightTemperatureKelvin { get; set; } = 4500;
}
