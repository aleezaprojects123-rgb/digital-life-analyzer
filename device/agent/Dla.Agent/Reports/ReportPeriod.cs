namespace Dla.Agent.Reports;

/// <summary>
/// A requested stretch of whole local calendar days: [FirstDay 00:00, day after LastDay 00:00) in <see cref="Zone"/>.
/// The caller picks it; nothing here looks at today's date.
/// </summary>
public sealed record ReportPeriod(ReportKind Kind, DateOnly FirstDay, int DayCount, TimeZoneInfo Zone)
{
    public DateOnly LastDay => FirstDay.AddDays(DayCount - 1);

    /// <summary>The first moment of the period (local midnight of the first day).</summary>
    public DateTimeOffset Start => MidnightOf(FirstDay);

    /// <summary>The first moment after the period (local midnight after the last day).</summary>
    public DateTimeOffset End => MidnightOf(FirstDay.AddDays(DayCount));

    /// <summary>InProgress while <paramref name="today"/> (the local date) is inside the period, Completed after its last day, Upcoming before its first.</summary>
    public ReportStatus StatusOn(DateOnly today) =>
        today > LastDay ? ReportStatus.Completed : today < FirstDay ? ReportStatus.Upcoming : ReportStatus.InProgress;

    public IEnumerable<DateOnly> Days => Enumerable.Range(0, DayCount).Select(i => FirstDay.AddDays(i));

    /// <summary>One calendar day. Used by the daily dashboard, which is cut out of a report of exactly that day.</summary>
    public static ReportPeriod Day(DateOnly day, TimeZoneInfo? zone = null) => new(ReportKind.Daily, day, 1, zone ?? TimeZoneInfo.Local);

    /// <summary>Seven days from <paramref name="startDate"/> (any weekday the caller chooses).</summary>
    public static ReportPeriod Week(DateOnly startDate, TimeZoneInfo? zone = null) => new(ReportKind.Weekly, startDate, 7, zone ?? TimeZoneInfo.Local);

    /// <summary>The Monday-to-Sunday week that contains <paramref name="anyDay"/>.</summary>
    public static ReportPeriod WeekContaining(DateOnly anyDay, TimeZoneInfo? zone = null) =>
        Week(anyDay.AddDays(-(((int)anyDay.DayOfWeek + 6) % 7)), zone);

    /// <summary>The whole calendar month: 28, 29, 30 or 31 days.</summary>
    public static ReportPeriod Month(int year, int month, TimeZoneInfo? zone = null)
    {
        if (month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month), "Month must be 1 to 12.");
        return new(ReportKind.Monthly, new DateOnly(year, month, 1), DateTime.DaysInMonth(year, month), zone ?? TimeZoneInfo.Local);
    }

    /// <summary>Local midnight of a day, as an exact moment. A day whose midnight does not exist (daylight saving) starts an hour later.</summary>
    public DateTimeOffset MidnightOf(DateOnly day)
    {
        var local = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        if (Zone.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Zone), TimeSpan.Zero);
    }
}
