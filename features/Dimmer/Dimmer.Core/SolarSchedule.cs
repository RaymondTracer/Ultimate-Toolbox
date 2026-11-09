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

/// <summary>Shared gamma-ramp color calculations for display backends.</summary>
public static class ColorTemperatureMath
{
    public static (double Red, double Green, double Blue) ToRgbFactors(int kelvin)
    {
        var temperature = Math.Clamp(kelvin, 4500, 6500) / 100.0;
        double red;
        double green;
        double blue;

        if (temperature <= 66)
        {
            red = 255;
            green = Math.Clamp(99.4708025861 * Math.Log(temperature) - 161.1195681661, 0, 255);
        }
        else
        {
            red = Math.Clamp(329.698727446 * Math.Pow(temperature - 60, -0.1332047592), 0, 255);
            green = Math.Clamp(288.1221695283 * Math.Pow(temperature - 60, -0.0755148492), 0, 255);
        }

        blue = temperature >= 66
            ? 255
            : Math.Clamp(138.5177312231 * Math.Log(temperature - 10) - 305.0447927307, 0, 255);
        return (red / 255.0, green / 255.0, blue / 255.0);
    }

    public static ushort[] ApplyToRgbRamp(
        ushort[] original,
        int size,
        (double Red, double Green, double Blue) factors)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (size < 0 || original.Length != size * 3)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "The ramp must contain three channels with the requested size.");
        }

        var result = new ushort[size * 3];
        for (var index = 0; index < size; index++)
        {
            result[index] = Scale(original[index], factors.Red);
            result[size + index] = Scale(original[size + index], factors.Green);
            result[(size * 2) + index] = Scale(original[(size * 2) + index], factors.Blue);
        }

        return result;
    }

    public static ushort[] ScaleRamp(ushort[] values, double factor)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new ushort[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            result[index] = Scale(values[index], factor);
        }

        return result;
    }

    private static ushort Scale(ushort value, double factor) =>
        (ushort)Math.Clamp(Math.Round(value * factor, MidpointRounding.AwayFromZero), 0, ushort.MaxValue);
}
