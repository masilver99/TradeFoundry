using System.Globalization;
using TradeFoundry.Core;

namespace TradeFoundry.Services.Backup;

/// <summary>
/// Turns a <see cref="BackupSchedule"/> into concrete instants. A local time that daylight saving skips
/// runs at the next valid minute; a local time that happens twice runs at its first occurrence, once.
/// </summary>
public static class BackupSchedulePlanner
{
    /// <summary>The latest scheduled instant at or before <paramref name="now"/>.</summary>
    public static DateTimeOffset LatestSlotAtOrBefore(BackupSchedule schedule, DateTimeOffset now)
    {
        var zone = schedule.ResolveTimeZone();
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime).AddDays(1);
        for (var i = 0; i < 16; i++, date = date.AddDays(-1))
        {
            if (!Matches(schedule, date)) continue;
            var slot = ToInstant(date, schedule.Time, zone);
            if (slot <= now) return slot;
        }

        throw new InvalidOperationException("No scheduled time found in the last two weeks.");
    }

    /// <summary>The first scheduled instant after <paramref name="now"/>.</summary>
    public static DateTimeOffset NextSlotAfter(BackupSchedule schedule, DateTimeOffset now)
    {
        var zone = schedule.ResolveTimeZone();
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime).AddDays(-1);
        for (var i = 0; i < 16; i++, date = date.AddDays(1))
        {
            if (!Matches(schedule, date)) continue;
            var slot = ToInstant(date, schedule.Time, zone);
            if (slot > now) return slot;
        }

        throw new InvalidOperationException("No scheduled time found in the next two weeks.");
    }

    /// <summary>Converts a local date and time to an instant, resolving daylight-saving gaps and overlaps.</summary>
    public static DateTimeOffset ToInstant(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        for (var guard = 0; zone.IsInvalidTime(local) && guard < 24 * 60; guard++) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static bool Matches(BackupSchedule schedule, DateOnly date) =>
        schedule.Frequency != BackupFrequency.Weekly || date.DayOfWeek == schedule.Day;
}

/// <summary>
/// Chooses which scheduled backups to delete. Only this installation's scheduled backups are ever
/// candidates; manual backups, other installations' backups and the newest candidate are always kept.
/// </summary>
public static class BackupRetention
{
    public static IReadOnlyList<BackupListing> SelectForPruning(IReadOnlyList<BackupListing> listings, BackupSchedule schedule)
    {
        var zone = schedule.ResolveTimeZone();
        var candidates = listings
            .Where(listing => listing.FromThisInstallation && listing.Sidecar.Trigger == BackupTrigger.Scheduled)
            .OrderByDescending(listing => listing.Sidecar.CreatedUtc)
            .ToList();
        if (candidates.Count == 0) return [];

        var keep = new HashSet<BackupListing> { candidates[0] };
        DateOnly LocalDate(BackupListing listing) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(listing.Sidecar.CreatedUtc, zone).DateTime);
        void KeepNewestPer<TKey>(Func<DateOnly, TKey> period, int count) where TKey : notnull
        {
            foreach (var newest in candidates.GroupBy(listing => period(LocalDate(listing))).Take(Math.Max(0, count)).Select(group => group.First()))
                keep.Add(newest);
        }

        KeepNewestPer(date => date, schedule.KeepDaily);
        KeepNewestPer(date => (ISOWeek.GetYear(date.ToDateTime(TimeOnly.MinValue)), ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue))), schedule.KeepWeekly);
        KeepNewestPer(date => (date.Year, date.Month), schedule.KeepMonthly);
        return candidates.Where(listing => !keep.Contains(listing)).ToList();
    }
}
