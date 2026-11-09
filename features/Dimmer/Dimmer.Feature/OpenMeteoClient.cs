using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using UltimateToolbox.Dimmer;

namespace UltimateToolbox.Dimmer.Feature;

internal sealed class OpenMeteoClient : IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public OpenMeteoClient()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UltimateToolbox", "1.0"));
    }

    public async Task<ResolvedSunSchedule> ResolveCityAsync(string city, CancellationToken cancellationToken)
    {
        var normalizedCity = city.Trim();
        if (normalizedCity.Length < 2)
        {
            throw new InvalidOperationException("Enter a city, region, or country name with at least two characters.");
        }

        using var geocoding = await FindLocationAsync(normalizedCity, cancellationToken);
        var location = ReadLocation(geocoding.RootElement);
        var solarDays = await FetchSolarDaysAsync(location.Latitude, location.Longitude, cancellationToken);
        var zone = GetString(geocoding.RootElement, "timezone");
        var label = FormatLocationLabel(geocoding.RootElement);
        return new ResolvedSunSchedule(
            normalizedCity,
            label,
            string.IsNullOrWhiteSpace(zone) ? solarDays.TimeZone : zone,
            location.Latitude,
            location.Longitude,
            solarDays.Events,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public async Task<SunScheduleRefresh> RefreshAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        ValidateCoordinates(latitude, longitude);
        var result = await FetchSolarDaysAsync(latitude, longitude, cancellationToken);
        return new SunScheduleRefresh(result.TimeZone, result.Events, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<JsonDocument> FindLocationAsync(string city, CancellationToken cancellationToken)
    {
        foreach (var candidate in GetSearchCandidates(city))
        {
            var url = "https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(candidate) +
                "&count=1&language=en";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);

            if (document.RootElement.TryGetProperty("results", out var results) &&
                results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0)
            {
                return document;
            }

            document.Dispose();
        }

        throw new InvalidOperationException("No matching city was found. Add the state, region, or country and try again.");
    }

    private async Task<(IReadOnlyList<SolarDay> Events, string TimeZone)> FetchSolarDaysAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        ValidateCoordinates(latitude, longitude);
        var lat = latitude.ToString("0.######", CultureInfo.InvariantCulture);
        var lon = longitude.ToString("0.######", CultureInfo.InvariantCulture);
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
            "&daily=sunrise,sunset&forecast_days=7&timezone=auto&timeformat=unixtime";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("daily", out var daily) ||
            !daily.TryGetProperty("sunrise", out var sunriseValues) ||
            !daily.TryGetProperty("sunset", out var sunsetValues) ||
            sunriseValues.ValueKind != JsonValueKind.Array ||
            sunsetValues.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The solar service returned no sunrise and sunset times.");
        }

        var events = new List<SolarDay>();
        var count = Math.Min(sunriseValues.GetArrayLength(), sunsetValues.GetArrayLength());
        for (var index = 0; index < count; index++)
        {
            if (!TryGetUnixSeconds(sunriseValues[index], out var sunrise) ||
                !TryGetUnixSeconds(sunsetValues[index], out var sunset) ||
                sunset <= sunrise)
            {
                continue;
            }

            try
            {
                events.Add(new SolarDay(
                    DateTimeOffset.FromUnixTimeSeconds(sunrise),
                    DateTimeOffset.FromUnixTimeSeconds(sunset)));
            }
            catch (ArgumentOutOfRangeException)
            {
                // Ignore invalid service values; the caller retains its previous cache if none remain.
            }
        }

        if (events.Count == 0)
        {
            throw new InvalidOperationException("The solar service returned no usable daily events.");
        }

        return (events, GetString(document.RootElement, "timezone"));
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var reason = "";
        try
        {
            using var document = JsonDocument.Parse(body);
            reason = GetString(document.RootElement, "reason");
        }
        catch (JsonException)
        {
            // Use the HTTP status if the response body is not an API error object.
        }

        throw new HttpRequestException(
            string.IsNullOrWhiteSpace(reason)
                ? $"The solar service returned HTTP {(int)response.StatusCode}."
                : $"The solar service rejected the request: {reason}",
            null,
            response.StatusCode);
    }

    private static IEnumerable<string> GetSearchCandidates(string city)
    {
        yield return city;
        if (city.Contains(','))
        {
            yield break;
        }

        var separator = city.LastIndexOfAny([' ', '\t']);
        var attempt = 0;
        while (separator > 0 && attempt < 4)
        {
            yield return city[..separator].Trim() + ", " + city[(separator + 1)..].Trim();
            separator = city.LastIndexOfAny([' ', '\t'], separator - 1);
            attempt++;
        }
    }

    private static (double Latitude, double Longitude) ReadLocation(JsonElement root)
    {
        var results = root.GetProperty("results");
        var location = results[0];
        var latitude = GetDouble(location, "latitude");
        var longitude = GetDouble(location, "longitude");
        ValidateCoordinates(latitude, longitude);
        return (latitude, longitude);
    }

    private static void ValidateCoordinates(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180)
        {
            throw new InvalidOperationException("The location service returned invalid coordinates.");
        }
    }

    private static string FormatLocationLabel(JsonElement root)
    {
        var location = root.GetProperty("results")[0];
        var parts = new[]
        {
            GetString(location, "name"),
            GetString(location, "admin1"),
            GetString(location, "country")
        };
        var distinctParts = parts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", distinctParts);
    }

    private static bool TryGetUnixSeconds(JsonElement element, out long seconds)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out seconds))
        {
            return seconds > 0;
        }

        seconds = 0;
        return false;
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double GetDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetDouble(out var result)
            ? result
            : double.NaN;
}

internal sealed record ResolvedSunSchedule(
    string City,
    string ResolvedLocation,
    string TimeZone,
    double Latitude,
    double Longitude,
    IReadOnlyList<SolarDay> SolarDays,
    long FetchedAtUtc);

internal sealed record SunScheduleRefresh(string TimeZone, IReadOnlyList<SolarDay> SolarDays, long FetchedAtUtc);
