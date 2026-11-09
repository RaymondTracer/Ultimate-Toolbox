namespace UltimateToolbox.Dimmer;

/// <summary>A solar day's event times. Sunrise and sunset are UTC instants.</summary>
public sealed record SolarDay(DateTimeOffset Sunrise, DateTimeOffset Sunset);

/// <summary>Shared schedule calculations for brightness and color temperature.</summary>
public static class SolarSchedule
{
    public const int NeutralTemperatureKelvin = 6500;

    /// <summary>
    /// Computes the smooth daylight amount for the current time.
    /// A result of zero is night, one is day, and values between them are fades.
    /// </summary>
    public static bool TryGetDayBlend(
        DateTimeOffset now,
        IEnumerable<SolarDay> solarDays,
        int transitionMinutes,
        out double dayBlend)
    {
        ArgumentNullException.ThrowIfNull(solarDays);
        dayBlend = 0;

        var days = solarDays
            .Where(day => day.Sunset > day.Sunrise)
            .OrderBy(day => day.Sunrise)
            .ToArray();
        if (days.Length == 0)
        {
            return false;
        }

        var configuredTransition = TimeSpan.FromMinutes(Math.Clamp(transitionMinutes, 1, 240));
        foreach (var day in days)
        {
            if (now < day.Sunrise)
            {
                return true;
            }

            var daylight = day.Sunset - day.Sunrise;
            var transition = TimeSpan.FromTicks(Math.Min(
                configuredTransition.Ticks,
                Math.Max(TimeSpan.FromSeconds(1).Ticks, daylight.Ticks / 2)));

            if (now < day.Sunrise + transition)
            {
                dayBlend = Smooth((now - day.Sunrise).TotalSeconds / transition.TotalSeconds);
                return true;
            }

            if (now < day.Sunset)
            {
                dayBlend = 1;
                return true;
            }

            if (now < day.Sunset + transition)
            {
                dayBlend = 1 - Smooth((now - day.Sunset).TotalSeconds / transition.TotalSeconds);
                return true;
            }
        }

        return true;
    }

    public static int BlendBrightness(int nightBrightness, int dayBrightness, double dayBlend)
    {
        var value = Lerp(Math.Clamp(nightBrightness, 0, 100), Math.Clamp(dayBrightness, 0, 100), dayBlend);
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Blends scheduled temperatures. A null endpoint means no tint at that
    /// endpoint; during a transition it uses neutral white as the interpolation
    /// baseline so the selected tint fades in or out with the brightness ramp.
    /// </summary>
    public static int? BlendTemperature(int? nightTemperature, int? dayTemperature, double dayBlend)
    {
        if (nightTemperature is null && dayTemperature is null)
        {
            return null;
        }

        var amount = Math.Clamp(dayBlend, 0, 1);
        if ((amount == 0 && nightTemperature is null) || (amount == 1 && dayTemperature is null))
        {
            return null;
        }

        var night = Math.Clamp(nightTemperature ?? NeutralTemperatureKelvin, 4500, NeutralTemperatureKelvin);
        var day = Math.Clamp(dayTemperature ?? NeutralTemperatureKelvin, 4500, NeutralTemperatureKelvin);
        var blended = Lerp(night, day, amount);
        return Math.Clamp((int)Math.Round(blended, MidpointRounding.AwayFromZero), 4500, NeutralTemperatureKelvin);
    }

    private static double Smooth(double amount)
    {
        var clamped = Math.Clamp(amount, 0, 1);
        return clamped * clamped * (3 - (2 * clamped));
    }

    private static double Lerp(double from, double to, double amount) =>
        from + ((to - from) * Math.Clamp(amount, 0, 1));
}
