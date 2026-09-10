namespace TradeFoundry.Core;

public static class BarIntervals
{
    public const string Auto = "auto";
    public const string Source = "source";

    public static bool IsAuto(string? value) => string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), Auto, StringComparison.OrdinalIgnoreCase);

    public static bool TryNormalize(string? value, out string normalized, bool allowSource = true)
    {
        normalized = string.Empty;
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text) || text == Source)
        {
            if (!allowSource) return false;
            normalized = Source;
            return true;
        }

        var digitsLength = 0;
        while (digitsLength < text.Length && char.IsDigit(text[digitsLength])) digitsLength++;
        if (digitsLength == 0 || !int.TryParse(text[..digitsLength], out var amount) || amount <= 0)
            return false;

        var suffix = text[digitsLength..] switch
        {
            "" or "m" or "min" or "mins" or "minute" or "minutes" => "m",
            "h" or "hr" or "hrs" or "hour" or "hours" => "h",
            "d" or "day" or "days" => "d",
            "w" or "wk" or "wks" or "week" or "weeks" => "w",
            _ => string.Empty
        };
        if (suffix.Length == 0) return false;

        int minutes;
        try
        {
            minutes = suffix switch
            {
                "m" => amount,
                "h" => checked(amount * 60),
                "d" => checked(amount * 1440),
                "w" => checked(amount * 10080),
                _ => 0
            };
        }
        catch (OverflowException)
        {
            return false;
        }
        if (minutes < 1) return false;

        normalized = Format(minutes);
        return true;
    }

    public static string Normalize(string? value, bool allowSource = true)
    {
        if (TryNormalize(value, out var normalized, allowSource)) return normalized;
        throw new FormatException("Bar interval must be at least 1 minute, such as 1m, 5m, 30m, 1h, or 1d.");
    }

    public static bool TryGetMinutes(string? value, out int minutes)
    {
        minutes = 0;
        if (!TryNormalize(value, out var normalized, allowSource: false)) return false;
        var digitsLength = 0;
        while (digitsLength < normalized.Length && char.IsDigit(normalized[digitsLength])) digitsLength++;
        if (digitsLength == 0 || !int.TryParse(normalized[..digitsLength], out var amount)) return false;
        var suffix = normalized[digitsLength..];
        try
        {
            minutes = suffix switch
            {
                "m" => amount,
                "h" => checked(amount * 60),
                "d" => checked(amount * 1440),
                "w" => checked(amount * 10080),
                _ => 0
            };
        }
        catch (OverflowException)
        {
            minutes = 0;
        }
        return minutes > 0;
    }

    public static string Format(int minutes)
    {
        if (minutes < 1) throw new ArgumentOutOfRangeException(nameof(minutes));
        if (minutes % 10080 == 0) return $"{minutes / 10080}w";
        if (minutes % 1440 == 0) return $"{minutes / 1440}d";
        if (minutes % 60 == 0) return $"{minutes / 60}h";
        return $"{minutes}m";
    }

    public static string Label(string interval)
    {
        if (!TryGetMinutes(interval, out var minutes))
            return string.Equals(interval, Source, StringComparison.OrdinalIgnoreCase) ? "source interval" : interval;
        if (minutes % 10080 == 0) return $"{minutes / 10080} week{(minutes == 10080 ? string.Empty : "s")}";
        if (minutes % 1440 == 0) return $"{minutes / 1440} day{(minutes == 1440 ? string.Empty : "s")}";
        if (minutes % 60 == 0) return $"{minutes / 60} hour{(minutes == 60 ? string.Empty : "s")}";
        return $"{minutes} minute{(minutes == 1 ? string.Empty : "s")}";
    }
}
