using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace IsItNiceOut.Services;

public class WeatherService(HttpClient http)
{
    // US-only typeahead
    public async Task<List<GeoLocation>> SearchUSTownsAsync(string query, int count = 8)
    {
        if (query.Length < 2) return [];
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&count={count}&language=en&format=json&countryCode=US";
        var result = await http.GetFromJsonAsync<GeocodingResponse>(url);
        return result?.Results ?? [];
    }

    // Single best match (US only)
    public async Task<GeoLocation?> SearchLocationAsync(string city)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=en&format=json&countryCode=US";
        var result = await http.GetFromJsonAsync<GeocodingResponse>(url);
        return result?.Results?.FirstOrDefault();
    }

    // Hourly view for a specific day (full detail + prior precip for MTB check)
    public async Task<HourlyForecast?> GetHourlyForecastAsync(double lat, double lon, DateOnly date)
    {
        // past_days=1 gives yesterday's precip for the "prior 24h dry" MTB check
        var url = $"https://api.open-meteo.com/v1/forecast" +
                  $"?latitude={lat}&longitude={lon}" +
                  $"&hourly=temperature_2m,apparent_temperature,weather_code,precipitation_probability,windspeed_10m,precipitation" +
                  $"&forecast_days=16&past_days=1&timezone=auto&temperature_unit=fahrenheit&wind_speed_unit=mph&precipitation_unit=inch";

        var raw = await http.GetFromJsonAsync<OpenMeteoHourlyResponse>(url);
        if (raw?.Hourly == null) return null;

        var hours     = new List<HourForecast>();
        var allPrecip = new Dictionary<DateTime, double>();

        int n = raw.Hourly.Time.Count;
        for (int i = 0; i < n; i++)
        {
            var dt     = DateTime.Parse(raw.Hourly.Time[i]);
            double precip = raw.Hourly.Precipitation.Count > i ? raw.Hourly.Precipitation[i] : 0;
            allPrecip[dt] = precip;

            // Include today AND tomorrow so a late-evening window can span midnight
            var dtDate = DateOnly.FromDateTime(dt);
            if (dtDate != date && dtDate != date.AddDays(1)) continue;
            hours.Add(new HourForecast
            {
                Time              = dt,
                Temperature       = raw.Hourly.Temperature[i],
                ApparentTemp      = raw.Hourly.ApparentTemperature[i],
                WeatherCode       = raw.Hourly.WeatherCode[i],
                PrecipProbability = raw.Hourly.PrecipitationProbability[i],
                WindSpeedMph      = raw.Hourly.WindSpeed[i],
                Precipitation     = precip,
            });
        }

        return new HourlyForecast { Date = date, Hours = hours, AllPrecip = allPrecip };
    }

    // 10-day daily forecast + MTB suitability
    public async Task<WeatherForecast?> GetForecastAsync(double lat, double lon)
    {
        // past_days=1 gives yesterday's hourly precip for accurate "prior 24h" MTB check
        var url = $"https://api.open-meteo.com/v1/forecast" +
                  $"?latitude={lat}&longitude={lon}" +
                  $"&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_sum,precipitation_probability_max,windspeed_10m_max,sunrise,sunset,relative_humidity_2m_max,relative_humidity_2m_min" +
                  $"&hourly=temperature_2m,precipitation" +
                  $"&forecast_days=16&past_days=1" +
                  $"&timezone=auto&temperature_unit=fahrenheit&wind_speed_unit=mph&precipitation_unit=inch";

        var raw = await http.GetFromJsonAsync<OpenMeteoResponse>(url);
        if (raw?.Daily == null) return null;

        // Build hourly lookup: DateTime (truncated to hour) → (temp, precip)
        var hourly = new Dictionary<DateTime, (double Temp, double Precip)>();
        if (raw.Hourly != null)
        {
            int n = Math.Min(raw.Hourly.Time.Count,
                    Math.Min(raw.Hourly.Temperature.Count, raw.Hourly.Precipitation.Count));
            for (int i = 0; i < n; i++)
            {
                var dt = DateTime.Parse(raw.Hourly.Time[i]);
                hourly[dt] = (raw.Hourly.Temperature[i], raw.Hourly.Precipitation[i]);
            }
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var days  = new List<DayForecast>();

        for (int i = 0; i < raw.Daily.Time.Count; i++)
        {
            var date = DateOnly.Parse(raw.Daily.Time[i]);
            if (date < today) continue; // skip the past_day entry

            DateTime sunrise = raw.Daily.Sunrise.Count > i ? DateTime.Parse(raw.Daily.Sunrise[i]) : default;
            DateTime sunset  = raw.Daily.Sunset.Count  > i ? DateTime.Parse(raw.Daily.Sunset[i])  : default;

            var day = new DayForecast
            {
                Date                = date,
                WeatherCode         = raw.Daily.WeatherCode[i],
                TempMax             = raw.Daily.TemperatureMax[i],
                TempMin             = raw.Daily.TemperatureMin[i],
                PrecipitationInches = raw.Daily.PrecipitationSum[i],
                PrecipProbability   = raw.Daily.PrecipitationProbabilityMax[i],
                WindSpeedMph        = raw.Daily.WindSpeedMax[i],
                Sunrise             = sunrise,
                Sunset              = sunset,
                // Min humidity = daytime (warm/dry), Max = nighttime (cool/moist)
                HumidityDay   = raw.Daily.HumidityMin.Count  > i ? raw.Daily.HumidityMin[i]  : -1,
                HumidityNight = raw.Daily.HumidityMax.Count  > i ? raw.Daily.HumidityMax[i]  : -1,
            };

            day.IsMtbDay = ComputeMtbDay(day, hourly);
            days.Add(day);
        }

        // Camping + fire pit checks (camping needs next-day data)
        for (int j = 0; j < days.Count; j++)
        {
            days[j].IsCampingDay  = ComputeCampingDay(days[j], j + 1 < days.Count ? days[j + 1] : null, hourly);
            days[j].IsFirePitDay  = ComputeFirePitDay(days[j]);
        }

        return new WeatherForecast { Days = days };
    }

    // ── MTB suitability ────────────────────────────────────────────────────
    static bool ComputeMtbDay(DayForecast day, Dictionary<DateTime, (double Temp, double Precip)> hourly)
    {
        // Rideable window = intersection of [7am, 9pm] and [sunrise, sunset]
        var date      = day.Date;
        var window7am = new DateTime(date.Year, date.Month, date.Day, 7,  0, 0);
        var window9pm = new DateTime(date.Year, date.Month, date.Day, 21, 0, 0);

        var rideStart = day.Sunrise != default && day.Sunrise > window7am ? day.Sunrise : window7am;
        var rideEnd   = day.Sunset  != default && day.Sunset  < window9pm ? day.Sunset  : window9pm;

        if (rideStart >= rideEnd) return false;

        // No precipitation in the 24 hours before rideStart
        var priorEnd   = new DateTime(rideStart.Year, rideStart.Month, rideStart.Day, rideStart.Hour, 0, 0);
        var priorStart = priorEnd.AddHours(-24);
        for (var t = priorStart; t < priorEnd; t = t.AddHours(1))
        {
            if (hourly.TryGetValue(t, out var ph) && ph.Precip > 0) return false;
        }

        // During rideable window: no precip AND at least one hour ≥ 50°F
        bool warmEnough = false;
        var  slotStart  = new DateTime(rideStart.Year, rideStart.Month, rideStart.Day, rideStart.Hour, 0, 0);
        for (var t = slotStart; t < rideEnd; t = t.AddHours(1))
        {
            if (hourly.TryGetValue(t, out var rh))
            {
                if (rh.Precip > 0) return false;
                if (rh.Temp >= 50) warmEnough = true;
            }
        }

        return warmEnough;
    }

    // ── Camping suitability ────────────────────────────────────────────────
    // Criteria: overnight low 55–65°F, 0% precip on camping day and next day,
    //           no precip in the 24 hours before midnight of the camping day.
    static bool ComputeCampingDay(DayForecast day, DayForecast? nextDay,
                                  Dictionary<DateTime, (double Temp, double Precip)> hourly)
    {
        if (nextDay == null) return false;
        if (day.TempMin < 55 || day.TempMin > 65) return false;
        if (day.PrecipProbability > 0 || nextDay.PrecipProbability > 0) return false;

        // No actual precip in the 24 h before midnight of the camping day
        var date = day.Date;
        var midnight = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0);
        for (var t = midnight.AddHours(-24); t < midnight; t = t.AddHours(1))
        {
            if (hourly.TryGetValue(t, out var ph) && ph.Precip > 0) return false;
        }

        return true;
    }

    // ── Fire pit suitability ───────────────────────────────────────────────
    // Criteria: 0% precip probability, daytime high 65–80°F, overnight low 50–60°F.
    static bool ComputeFirePitDay(DayForecast day) =>
        day.PrecipProbability == 0 &&
        day.TempMax >= 65 && day.TempMax <= 80 &&
        day.TempMin >= 50 && day.TempMin <= 60;

    // ── Weather code → emoji + description ────────────────────────────────
    public static (string Emoji, string Description) GetWeatherInfo(int code) => code switch
    {
        0         => ("☀️",  "Clear Sky"),
        1         => ("🌤️", "Mostly Clear"),
        2         => ("⛅",  "Partly Cloudy"),
        3         => ("☁️",  "Overcast"),
        45 or 48  => ("🌫️", "Foggy"),
        51 or 53 or 55 => ("🌦️", "Drizzle"),
        56 or 57  => ("🌧️", "Freezing Drizzle"),
        61 or 63 or 65 => ("🌧️", "Rain"),
        66 or 67  => ("🌧️", "Freezing Rain"),
        71 or 73 or 75 => ("🌨️", "Snow"),
        77        => ("🌨️", "Snow Grains"),
        80 or 81 or 82 => ("🌦️", "Rain Showers"),
        85 or 86  => ("🌨️", "Snow Showers"),
        95        => ("⛈️",  "Thunderstorm"),
        96 or 99  => ("⛈️",  "Thunderstorm + Hail"),
        _         => ("🌡️", "Unknown")
    };
}

// ── Daily forecast models ──────────────────────────────────────────────────

public class WeatherForecast
{
    public List<DayForecast> Days { get; set; } = [];
}

public class DayForecast
{
    public DateOnly  Date               { get; set; }
    public int       WeatherCode        { get; set; }
    public double    TempMax            { get; set; }
    public double    TempMin            { get; set; }
    public double    PrecipitationInches{ get; set; }
    public int       PrecipProbability  { get; set; }
    public double    WindSpeedMph       { get; set; }
    public DateTime  Sunrise            { get; set; }
    public DateTime  Sunset             { get; set; }
    public bool      IsMtbDay           { get; set; }
    public bool      IsCampingDay       { get; set; }
    public bool      IsFirePitDay       { get; set; }
    public int       HumidityDay        { get; set; } = -1; // daytime (min)
    public int       HumidityNight      { get; set; } = -1; // nighttime (max)
}

// ── Geo models ────────────────────────────────────────────────────────────

public class GeoLocation
{
    [JsonPropertyName("name")]      public string  Name    { get; set; } = "";
    [JsonPropertyName("country")]   public string  Country { get; set; } = "";
    [JsonPropertyName("admin1")]    public string? Region  { get; set; }
    [JsonPropertyName("latitude")]  public double  Latitude  { get; set; }
    [JsonPropertyName("longitude")] public double  Longitude { get; set; }
}

public class GeocodingResponse
{
    [JsonPropertyName("results")] public List<GeoLocation>? Results { get; set; }
}

// ── Open-Meteo daily+hourly response ─────────────────────────────────────

public class OpenMeteoResponse
{
    [JsonPropertyName("daily")]  public OpenMeteoDaily?  Daily  { get; set; }
    [JsonPropertyName("hourly")] public OpenMeteoDailyHourly? Hourly { get; set; }
}

public class OpenMeteoDaily
{
    [JsonPropertyName("time")]                          public List<string> Time              { get; set; } = [];
    [JsonPropertyName("weather_code")]                  public List<int>    WeatherCode        { get; set; } = [];
    [JsonPropertyName("temperature_2m_max")]            public List<double> TemperatureMax     { get; set; } = [];
    [JsonPropertyName("temperature_2m_min")]            public List<double> TemperatureMin     { get; set; } = [];
    [JsonPropertyName("precipitation_sum")]             public List<double> PrecipitationSum   { get; set; } = [];
    [JsonPropertyName("precipitation_probability_max")] public List<int>    PrecipitationProbabilityMax { get; set; } = [];
    [JsonPropertyName("windspeed_10m_max")]             public List<double> WindSpeedMax       { get; set; } = [];
    [JsonPropertyName("sunrise")]                       public List<string> Sunrise            { get; set; } = [];
    [JsonPropertyName("sunset")]                        public List<string> Sunset             { get; set; } = [];
    [JsonPropertyName("relative_humidity_2m_max")]      public List<int>    HumidityMax        { get; set; } = [];
    [JsonPropertyName("relative_humidity_2m_min")]      public List<int>    HumidityMin        { get; set; } = [];
}

// Slim hourly model for MTB computation (temp + precip only)
public class OpenMeteoDailyHourly
{
    [JsonPropertyName("time")]          public List<string> Time          { get; set; } = [];
    [JsonPropertyName("temperature_2m")]public List<double> Temperature   { get; set; } = [];
    [JsonPropertyName("precipitation")] public List<double> Precipitation { get; set; } = [];
}

// ── Hourly view models (full detail for drill-down) ───────────────────────

public class HourlyForecast
{
    public DateOnly                    Date      { get; set; }
    public List<HourForecast>          Hours     { get; set; } = [];
    /// <summary>All hourly precip including prior day, keyed by truncated DateTime hour.</summary>
    public Dictionary<DateTime,double> AllPrecip { get; set; } = [];
}

public class HourForecast
{
    public DateTime Time              { get; set; }
    public double   Temperature       { get; set; }
    public double   ApparentTemp      { get; set; }
    public int      WeatherCode       { get; set; }
    public int      PrecipProbability { get; set; }
    public double   WindSpeedMph      { get; set; }
    public double   Precipitation     { get; set; }
}

public class OpenMeteoHourlyResponse
{
    [JsonPropertyName("hourly")] public OpenMeteoHourly? Hourly { get; set; }
}

public class OpenMeteoHourly
{
    [JsonPropertyName("time")]                      public List<string> Time                   { get; set; } = [];
    [JsonPropertyName("temperature_2m")]            public List<double> Temperature            { get; set; } = [];
    [JsonPropertyName("apparent_temperature")]      public List<double> ApparentTemperature    { get; set; } = [];
    [JsonPropertyName("weather_code")]              public List<int>    WeatherCode             { get; set; } = [];
    [JsonPropertyName("precipitation_probability")] public List<int>    PrecipitationProbability{ get; set; } = [];
    [JsonPropertyName("windspeed_10m")]             public List<double> WindSpeed               { get; set; } = [];
    [JsonPropertyName("precipitation")]             public List<double> Precipitation           { get; set; } = [];
}
