using UltimateToolbox.Dimmer;

var solarDay = new SolarDay(
    new DateTimeOffset(2026, 11, 10, 6, 0, 0, TimeSpan.Zero),
    new DateTimeOffset(2026, 11, 10, 18, 0, 0, TimeSpan.Zero));
const int transitionMinutes = 60;

CheckBlend("night before sunrise", At(5, 0), solarDay, transitionMinutes, 0);
CheckBlend("start of sunrise transition", At(6, 0), solarDay, transitionMinutes, 0);
CheckBlend("mid sunrise transition is smooth", At(6, 30), solarDay, transitionMinutes, 0.5);
CheckBlend("day after sunrise transition", At(7, 0), solarDay, transitionMinutes, 1);
CheckBlend("day before sunset transition", At(17, 0), solarDay, transitionMinutes, 1);
CheckBlend("start of sunset transition", At(18, 0), solarDay, transitionMinutes, 1);
CheckBlend("mid sunset transition is smooth", At(18, 30), solarDay, transitionMinutes, 0.5);
CheckBlend("night after sunset transition", At(19, 0), solarDay, transitionMinutes, 0);

Equal("brightness uses shared schedule blend", 70, SolarSchedule.BlendBrightness(40, 80, 0.75));
Equal("both neutral temperature endpoints remain disabled", null, SolarSchedule.BlendTemperature(null, null, 0.5));
Equal("neutral night endpoint is restored", null, SolarSchedule.BlendTemperature(null, 4500, 0));
Equal("neutral night endpoint begins tint fade", 6500, SolarSchedule.BlendTemperature(null, 4500, 0.0001));
Equal("night temperature halfway through fade", 5500, SolarSchedule.BlendTemperature(null, 4500, 0.5));
Equal("selected night temperature is reached", 4500, SolarSchedule.BlendTemperature(null, 4500, 1));
Equal("neutral day endpoint is restored", null, SolarSchedule.BlendTemperature(4500, null, 1));
Equal("selected temperature endpoints blend together", 5250, SolarSchedule.BlendTemperature(4500, 6000, 0.5));

var identityRamp = new ushort[] { 0, 16384, 65535, 0, 16384, 65535, 0, 16384, 65535 };
SequenceEqual(
    "neutral temperature keeps the original RGB ramp",
    identityRamp,
    ColorTemperatureMath.ApplyToRgbRamp(identityRamp, 3, (1, 1, 1)));
SequenceEqual("zero-scale temperature ramp is black", new ushort[] { 0, 0, 0 }, ColorTemperatureMath.ScaleRamp([1, 32768, 65535], 0));
Near("4500 K red factor stays neutral", 1, ColorTemperatureMath.ToRgbFactors(4500).Red);
if (ColorTemperatureMath.ToRgbFactors(4500).Blue >= ColorTemperatureMath.ToRgbFactors(4500).Red)
{
    throw new InvalidOperationException("4500 K should reduce blue more strongly than red.");
}

if (SolarSchedule.TryGetDayBlend(At(12, 0), [], transitionMinutes, out _))
{
    throw new InvalidOperationException("An empty solar cache should report that no schedule can be evaluated.");
}

Console.WriteLine("Dimmer schedule regression checks passed.");
return;

static DateTimeOffset At(int hour, int minute) =>
    new(2026, 11, 10, hour, minute, 0, TimeSpan.Zero);

static void CheckBlend(string name, DateTimeOffset time, SolarDay day, int transitionMinutes, double expected)
{
    if (!SolarSchedule.TryGetDayBlend(time, [day], transitionMinutes, out var actual))
    {
        throw new InvalidOperationException($"{name}: the solar schedule was not evaluated.");
    }

    Near(name, expected, actual);
}

static void Near(string name, double expected, double actual)
{
    if (Math.Abs(expected - actual) > 0.000001)
    {
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}.");
    }
}

static void Equal<T>(string name, T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}.");
    }
}

static void SequenceEqual<T>(string name, IReadOnlyList<T> expected, IReadOnlyList<T> actual)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException($"{name}: sequences did not match.");
    }
}
