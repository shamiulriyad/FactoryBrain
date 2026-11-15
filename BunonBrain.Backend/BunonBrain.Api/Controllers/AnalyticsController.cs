using BunonBrain.Api.Authorization;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Analytics;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Analytics;
using BunonBrain.Infrastructure.Analytics;
using Microsoft.AspNetCore.Mvc;

namespace BunonBrain.Api.Controllers;

/// <summary>
/// Factory analytics and reports. Every route reads the existing modules'
/// records within the caller's factory / line scope; each section also
/// needs the permission of its data (production, machines, attendance,
/// overtime, assignments, camera AI, devices, alerts). Filters are the
/// same everywhere: factoryId, departmentId, floorId, lineId, workstationId,
/// machineId, shiftId, from, to (factory-local dates, yyyy-MM-dd), source
/// (REAL | SIMULATED), eventSource (DEVICE | MANUAL | SIMULATOR), family
/// (generic | garment, AI activity).
/// </summary>
[ApiController]
[Route("api/analytics")]
public class AnalyticsController : ControllerBase
{
    private const string Any = "any";

    private readonly IOverviewAnalytics _overview;
    private readonly IProductionAnalytics _production;
    private readonly IMachineAnalytics _machines;
    private readonly IWorkforceAnalytics _workforce;
    private readonly IActivityAnalytics _activity;
    private readonly IOperationalInsights _insights;
    private readonly IAnalyticsReports _reports;

    public AnalyticsController(IOverviewAnalytics overview, IProductionAnalytics production, IMachineAnalytics machines, IWorkforceAnalytics workforce,
        IActivityAnalytics activity, IOperationalInsights insights, IAnalyticsReports reports)
    {
        _overview = overview; _production = production; _machines = machines; _workforce = workforce; _activity = activity; _insights = insights; _reports = reports;
    }

    private static AnalyticsFilter F(Guid? factoryId, Guid? departmentId, Guid? floorId, string? lineId, Guid? workstationId, Guid? machineId, Guid? shiftId,
        DateOnly? from, DateOnly? to, string? source, string? eventSource, string? family) =>
        new(factoryId, departmentId, floorId, string.IsNullOrWhiteSpace(lineId) ? null : lineId, workstationId, machineId, shiftId, from, to, source, eventSource, family);

    /// <summary>GET /api/analytics/metrics — the metric definitions (source, formula, window, inclusions, denominator, missing data, kind).</summary>
    [HttpGet("metrics")]
    [RequirePermission(Permissions.ProductionView, Permissions.MonitoringView, Permissions.DevicesView, Permissions.AttendanceView, Permissions.OvertimeView,
        Permissions.WorkersView, Permissions.ReportsView)]
    public IActionResult Metrics() => Ok(MetricCatalog.All);

    /// <summary>GET /api/analytics/overview?… — workforce, production, machines now, overtime, open alerts, data coverage.</summary>
    [HttpGet("overview")]
    [RequirePermission(Permissions.ProductionView, Permissions.MonitoringView, Permissions.DevicesView, Permissions.AttendanceView, Permissions.OvertimeView,
        Permissions.WorkersView, Permissions.ReportsView)]
    public async Task<IActionResult> Overview([FromQuery] Guid? factoryId, [FromQuery] Guid? departmentId, [FromQuery] Guid? floorId, [FromQuery] string? lineId,
        [FromQuery] Guid? workstationId, [FromQuery] Guid? machineId, [FromQuery] Guid? shiftId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] string? source, [FromQuery] string? eventSource, CancellationToken ct)
        => AccessResults.From(this, await _overview.OverviewAsync(this.Caller(), F(factoryId, departmentId, floorId, lineId, workstationId, machineId, shiftId, from, to, source, eventSource, null), ct));

    [HttpGet("production")]
    [RequirePermission(Permissions.ProductionView, Permissions.MonitoringView)]
    public async Task<IActionResult> Production([FromQuery] Guid? factoryId, [FromQuery] Guid? departmentId, [FromQuery] Guid? floorId, [FromQuery] string? lineId,
        [FromQuery] Guid? workstationId, [FromQuery] Guid? machineId, [FromQuery] Guid? shiftId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] string? source, [FromQuery] string? eventSource, CancellationToken ct)
        => AccessResults.From(this, await _production.ProductionAsync(this.Caller(), F(factoryId, departmentId, floorId, lineId, workstationId, machineId, shiftId, from, to, source, eventSource, null), ct));

    [HttpGet("machines")]
    [RequirePermission(Permissions.MonitoringView, Permissions.DevicesView, Permissions.ProductionView)]
    public async Task<IActionResult> Machines([FromQuery] Guid? factoryId, [FromQuery] Guid? departmentId, [FromQuery] Guid? floorId, [FromQuery] string? lineId,
        [FromQuery] Guid? workstationId, [FromQuery] Guid? machineId, [FromQuery] Guid? shiftId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] string? source, [FromQuery] string? eventSource, CancellationToken ct)
        => AccessResults.From(this, await _machines.MachinesAsync(this.Caller(), F(factoryId, departmentId, floorId, lineId, workstationId, machineId, shiftId, from, to, source, eventSource, null), ct));

    [HttpGet("workforce")]
    [RequirePermission(Permissions.AttendanceView, Permissions.OvertimeView, Permissions.WorkersView)]
    public async Task<IActionResult> Workforce([FromQuery] Guid? factoryId, [FromQuery] Guid? departmentId, [FromQuery] Guid? floorId, [FromQuery] string? lineId,
        [FromQuery] Guid? workstationId, [FromQuery] Guid? shiftId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? source,
        [FromQuery] string? eventSource, CancellationToken ct)
        => AccessResults.From(this, await _workforce.WorkforceAsync(this.Caller(), F(factoryId, departmentId, floorId, lineId, workstationId, null, shiftId, from, to, source, eventSource, null), ct));

    [HttpGet("activity")]
    [RequirePermission(Permissions.MonitoringView)]
    public async Task<IActionResult> Activity([FromQuery] Guid? factoryId, [FromQuery] Guid? departmentId, [FromQuery] Guid? floorId, [FromQuery] string? lineId,
        [FromQuery] Guid? workstationId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? source, [FromQuery] string? eventSource,
        [FromQuery] string? family, CancellationToken ct)
        => AccessResults.From(this, await _activity.ActivityAsync(this.Caller(), F(factoryId, departmentId, floorId, lineId, workstationId, null, null, from, to, source, eventSource, family), ct));

    [HttpGet("insights")]
    [RequirePermission(Permissions.ProductionView, Permissions.MonitoringView, Permissions.DevicesView, Permissions.AttendanceView, Permissions.OvertimeView,
        Permissions.WorkersView, Permissions.ReportsView)]
    public async Task<IActionResult> Insights([FromQuery] Guid? factoryId, [FromQuery] Guid? departmentId, [FromQuery] Guid? floorId, [FromQuery] string? lineId,
        [FromQuery] Guid? workstationId, [FromQuery] Guid? machineId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? source, CancellationToken ct)
        => AccessResults.From(this, await _insights.InsightsAsync(this.Caller(), F(factoryId, departmentId, floorId, lineId, workstationId, machineId, null, from, to, source, null, null), ct));

    /// <summary>GET /api/analytics/reports — the report types and whether your role may generate them.</summary>
    [HttpGet("reports")]
    [RequirePermission(Permissions.ProductionView, Permissions.MonitoringView, Permissions.DevicesView, Permissions.AttendanceView, Permissions.OvertimeView,
        Permissions.WorkersView, Permissions.ReportsView)]
    public IActionResult Reports() => Ok(_reports.Types(this.Caller()));

    /// <summary>GET /api/analytics/reports/{type}?… — the report as JSON (audited).</summary>
    [HttpGet("reports/{type}")]
    [RequirePermission(Permissions.ProductionView, Permissions.MonitoringView, Permissions.DevicesView, Permissions.AttendanceView, Permissions.OvertimeView,
        Permissions.WorkersView, Permissions.ReportsView)]
    public async Task<IActionResult> Report([FromRoute] string type, [FromQuery] Guid? factoryId, [FromQuery] Guid? departmentId, [FromQuery] Guid? floorId,
        [FromQuery] string? lineId, [FromQuery] Guid? workstationId, [FromQuery] Guid? machineId, [FromQuery] Guid? shiftId, [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to, [FromQuery] string? source, [FromQuery] string? eventSource, [FromQuery] string? family, CancellationToken ct)
    {
        var filter = F(factoryId, departmentId, floorId, lineId, workstationId, machineId, shiftId, from, to, source, eventSource, family);
        var r = await _reports.GenerateAsync(this.Caller(), type, filter, ct);
        if (r.Outcome == AccessOutcome.Ok && _reports is AnalyticsReportService svc)
            await svc.AuditAsync(this.Caller(), "analytics.report_generated", r.Value!, $"{r.Value!.Title} generated", AccessResults.TraceId(this), factoryId, ct);
        return AccessResults.From(this, r);
    }

    /// <summary>GET /api/analytics/reports/{type}/export?format=csv|pdf&amp;… — the report as a file (audited; same scope and filters).</summary>
    [HttpGet("reports/{type}/export")]
    [RequirePermission(Permissions.ProductionView, Permissions.MonitoringView, Permissions.DevicesView, Permissions.AttendanceView, Permissions.OvertimeView,
        Permissions.WorkersView, Permissions.ReportsView)]
    public async Task<IActionResult> Export([FromRoute] string type, [FromQuery] string? format, [FromQuery] Guid? factoryId, [FromQuery] Guid? departmentId,
        [FromQuery] Guid? floorId, [FromQuery] string? lineId, [FromQuery] Guid? workstationId, [FromQuery] Guid? machineId, [FromQuery] Guid? shiftId,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? source, [FromQuery] string? eventSource, [FromQuery] string? family, CancellationToken ct)
    {
        var r = await _reports.ExportAsync(this.Caller(), type, format ?? "csv",
            F(factoryId, departmentId, floorId, lineId, workstationId, machineId, shiftId, from, to, source, eventSource, family), AccessResults.TraceId(this), ct);
        if (r.Outcome != AccessOutcome.Ok) return AccessResults.From(this, r);
        Response.Headers.CacheControl = "no-store";
        return File(r.Value!.Content, r.Value.ContentType, r.Value.FileName);
    }
}
