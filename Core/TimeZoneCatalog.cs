using System.Globalization;

namespace TradeFoundry.Core;

public sealed record TimeZoneOption(string Id, string Label);

public static class TimeZoneCatalog
{
    public const string Auto = "auto";

    private static readonly TimeZoneOption[] CommonOptions =
    {
        new("UTC", "UTC"),
        new("America/New_York", "Eastern Time (New York)"),
        new("America/Chicago", "Central Time (Chicago)"),
        new("America/Denver", "Mountain Time (Denver)"),
        new("America/Los_Angeles", "Pacific Time (Los Angeles)"),
        new("Europe/London", "London"),
        new("Europe/Berlin", "Central European Time (Berlin)"),
        new("Asia/Kolkata", "India Standard Time (Kolkata)"),
        new("Asia/Shanghai", "China Standard Time (Shanghai)"),
        new("Asia/Tokyo", "Japan Standard Time (Tokyo)"),
        new("Australia/Sydney", "Australian Eastern Time (Sydney)")
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UTC"] = "UTC",
        ["Etc/UTC"] = "UTC",
        ["GMT"] = "UTC",
        ["EST"] = "America/New_York",
        ["EDT"] = "America/New_York",
        ["Eastern"] = "America/New_York",
        ["Eastern Time"] = "America/New_York",
        ["Eastern Standard Time"] = "America/New_York",
        ["CST"] = "America/Chicago",
        ["CDT"] = "America/Chicago",
        ["Central"] = "America/Chicago",
        ["Central Time"] = "America/Chicago",
        ["Central Standard Time"] = "America/Chicago",
        ["MST"] = "America/Denver",
        ["MDT"] = "America/Denver",
        ["Mountain"] = "America/Denver",
        ["Mountain Time"] = "America/Denver",
        ["Mountain Standard Time"] = "America/Denver",
        ["PST"] = "America/Los_Angeles",
        ["PDT"] = "America/Los_Angeles",
        ["Pacific"] = "America/Los_Angeles",
        ["Pacific Time"] = "America/Los_Angeles",
        ["Pacific Standard Time"] = "America/Los_Angeles"
    };

    private static readonly Dictionary<string, string> IanaToWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UTC"] = "UTC",
        ["America/New_York"] = "Eastern Standard Time",
        ["America/Chicago"] = "Central Standard Time",
        ["America/Denver"] = "Mountain Standard Time",
        ["America/Los_Angeles"] = "Pacific Standard Time",
        ["Europe/London"] = "GMT Standard Time",
        ["Europe/Berlin"] = "W. Europe Standard Time",
        ["Asia/Kolkata"] = "India Standard Time",
        ["Asia/Shanghai"] = "China Standard Time",
        ["Asia/Tokyo"] = "Tokyo Standard Time",
        ["Australia/Sydney"] = "AUS Eastern Standard Time"
    };

    public static IReadOnlyList<TimeZoneOption> ForSelection(string? currentTimeZone)
    {
        var currentId = CanonicalId(currentTimeZone);
        var options = new List<TimeZoneOption>();
        if (!CommonOptions.Any(option => option.Id.Equals(currentId, StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(currentTimeZone))
            options.Add(new(currentId, $"Current journal timezone ({currentTimeZone.Trim()})"));

        options.AddRange(CommonOptions);
        return options;
    }

    public static IReadOnlyList<TimeZoneOption> ForImportSelection(string? currentTimeZone)
    {
        var options = new List<TimeZoneOption>
        {
            new(Auto, "Auto-detect source timezone (recommended)")
        };
        options.AddRange(ForSelection(currentTimeZone));
        return options;
    }

    public static bool IsAuto(string? timeZoneId) => string.Equals(timeZoneId?.Trim(), Auto, StringComparison.OrdinalIgnoreCase);

    public static string CanonicalId(string? timeZoneId)
    {
        var trimmed = timeZoneId?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return "UTC";
        if (IsAuto(trimmed)) return Auto;
        return Aliases.TryGetValue(trimmed, out var mapped) ? mapped : trimmed;
    }

    public static TimeZoneInfo Resolve(string? timeZoneId) => TryResolve(timeZoneId, out var timeZone) ? timeZone : TimeZoneInfo.Utc;

    public static bool TryResolve(string? timeZoneId, out TimeZoneInfo timeZone)
    {
        var trimmed = timeZoneId?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            timeZone = TimeZoneInfo.Utc;
            return true;
        }

        var canonical = CanonicalId(trimmed);
        var candidates = new[]
        {
            canonical,
            trimmed,
            IanaToWindows.TryGetValue(canonical, out var windowsId) ? windowsId : string.Empty
        };

        foreach (var candidate in candidates.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                timeZone = TimeZoneInfo.FindSystemTimeZoneById(candidate);
                return true;
            }
            catch (TimeZoneNotFoundException)
            {
                // Try the next platform-specific identifier.
            }
            catch (InvalidTimeZoneException)
            {
                // Try the next platform-specific identifier.
            }
        }

        timeZone = TimeZoneInfo.Utc;
        return false;
    }

    public static DateTimeOffset FromLocal(DateTime local, string? timeZoneId)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var offset = Resolve(timeZoneId).GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }

    public static DateTimeOffset Convert(DateTimeOffset value, string? timeZoneId) => TimeZoneInfo.ConvertTime(value, Resolve(timeZoneId));

    public static string Format(DateTimeOffset value, string? timeZoneId, string format) => Convert(value, timeZoneId).ToString(format, CultureInfo.CurrentCulture);

    public static string FormatWallClock(DateTimeOffset value, string? timeZoneId, string format = "yyyy-MM-ddTHH:mm:ss.fff") => Convert(value, timeZoneId).DateTime.ToString(format, CultureInfo.InvariantCulture);
}
