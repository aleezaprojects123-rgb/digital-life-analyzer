using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dla.Agent.Reports;

/// <summary>
/// The application-facing report operations. A caller (the future UI, or the local HTTP route) asks for "the current week" or "the previous
/// month" and never supplies a date: the dates, the time zone and the status all come from <see cref="ReportGenerator"/>. This layer
/// calculates nothing itself and stores nothing.
/// </summary>
public interface IReportService
{
    ActivityReport GetCurrentWeeklyReport();
    ActivityReport GetPreviousWeeklyReport();
    ActivityReport GetCurrentMonthlyReport();
    ActivityReport GetPreviousMonthlyReport();

    /// <summary>Today's platform times and content counts for the dashboard, calculated now from the events.</summary>
    DashboardToday GetTodayDashboard();
}

public sealed class ReportService : IReportService
{
    private readonly ReportGenerator _generator;

    public ReportService(ReportGenerator generator) => _generator = generator;

    public ActivityReport GetCurrentWeeklyReport() => _generator.GetCurrentWeeklyReport();
    public ActivityReport GetPreviousWeeklyReport() => _generator.GetPreviousWeeklyReport();
    public ActivityReport GetCurrentMonthlyReport() => _generator.GetCurrentMonthlyReport();
    public ActivityReport GetPreviousMonthlyReport() => _generator.GetPreviousMonthlyReport();
    public DashboardToday GetTodayDashboard() => DashboardToday.From(_generator.GetTodayReport());
}

/// <summary>The one way a report becomes JSON: camelCase names, enums as words (InProgress), dates as yyyy-MM-dd.</summary>
public static class ReportJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(ActivityReport report) => JsonSerializer.Serialize(report, Options);

    public static string Serialize(DashboardToday dashboard) => JsonSerializer.Serialize(dashboard, Options);
}
