using BunonBrain.Application.Access;
using BunonBrain.Application.Analytics;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Analytics;

namespace BunonBrain.Application.Abstractions.Interfaces;

// Factory analytics: one service per page, each reading the existing
// modules' records within the caller's factory / line scope and permissions.

public interface IOverviewAnalytics
{
    Task<AccessResult<FactoryOverviewDto>> OverviewAsync(AccessContext caller, AnalyticsFilter filter, CancellationToken ct);
}

public interface IProductionAnalytics
{
    Task<AccessResult<ProductionAnalyticsDto>> ProductionAsync(AccessContext caller, AnalyticsFilter filter, CancellationToken ct);
}

public interface IMachineAnalytics
{
    Task<AccessResult<MachineAnalyticsDto>> MachinesAsync(AccessContext caller, AnalyticsFilter filter, CancellationToken ct);
}

public interface IWorkforceAnalytics
{
    Task<AccessResult<WorkforceAnalyticsDto>> WorkforceAsync(AccessContext caller, AnalyticsFilter filter, CancellationToken ct);
}

public interface IActivityAnalytics
{
    Task<AccessResult<ActivityInsightsDto>> ActivityAsync(AccessContext caller, AnalyticsFilter filter, CancellationToken ct);
}

public interface IOperationalInsights
{
    Task<AccessResult<OperationalInsightsDto>> InsightsAsync(AccessContext caller, AnalyticsFilter filter, CancellationToken ct);
}

public interface IAnalyticsReports
{
    IReadOnlyList<ReportTypeDto> Types(AccessContext caller);
    Task<AccessResult<ReportDocument>> GenerateAsync(AccessContext caller, string type, AnalyticsFilter filter, CancellationToken ct);
    /// <summary>CSV or PDF of a report; audited.</summary>
    Task<AccessResult<ReportFile>> ExportAsync(AccessContext caller, string type, string format, AnalyticsFilter filter, string? traceId, CancellationToken ct);
}
