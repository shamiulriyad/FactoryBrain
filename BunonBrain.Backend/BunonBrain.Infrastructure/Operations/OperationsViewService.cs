using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Approvals;
using BunonBrain.Application.Dtos.Ask;
using BunonBrain.Application.Dtos.Operations;
using BunonBrain.Application.Iot;
using BunonBrain.Application.Monitoring;
using BunonBrain.Domain.Entities;
using BunonBrain.Domain.Enums;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Operations;

/// <summary>
/// Read-only views that combine records other modules already store. Only
/// REAL data decides a status: simulated alerts, machine states and
/// exceptions are left out of the floor rollup and the graph, and the
/// integration list counts them separately. Nothing here writes.
/// </summary>
public sealed class OperationsViewService : IOperationsViewService
{
    private const string Real = "REAL";
    private const int MaxInsights = 200;
    private static readonly TimeSpan Recent = TimeSpan.FromHours(24);

    private readonly BunonBrainDbContext _db;
    public OperationsViewService(BunonBrainDbContext db) { _db = db; }

    // ------------------------------------------------------------------
    // Insights
    // ------------------------------------------------------------------

    public async Task<InsightListResponse> InsightsAsync(string? agentId, string? risk, string? stage, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, MaxInsights);
        var q = _db.Insights.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(agentId)) q = q.Where(i => i.AgentId == agentId);
        if (TryStage(stage, out var st)) q = q.Where(i => i.Stage == st);
        // The recommendation is an owned value; filter its risk in memory over a bounded window.
        var rows = await q.OrderByDescending(i => i.Pinned).ThenByDescending(i => i.CreatedAt).Take(MaxInsights).ToListAsync(ct);
        if (TryRisk(risk, out var tier)) rows = rows.Where(i => i.Recommendation.RiskTier == tier).ToList();
        return new InsightListResponse(rows.Count, rows.Take(limit).Select(ToDto).ToList());
    }

    public async Task<InsightDto?> InsightAsync(string id, CancellationToken ct)
    {
        var row = await _db.Insights.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        return row is null ? null : ToDto(row);
    }

    private static bool TryStage(string? raw, out Stage stage)
    {
        stage = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        foreach (var s in Enum.GetValues<Stage>())
            if (StageNames.ToWire(s) == raw.Trim().ToLowerInvariant()) { stage = s; return true; }
        return false;
    }

    private static bool TryRisk(string? raw, out RiskTier tier) => Enum.TryParse(raw?.Trim(), ignoreCase: true, out tier) && Enum.IsDefined(tier);

    private static InsightDto ToDto(Insight i) => new(
        i.Id, i.AgentId, i.AgentLabel, i.Title, i.TitleBn, i.Finding, i.FindingBn,
        i.Factors.Select(f => new AskFactor(f.Label, f.LabelBn, f.Magnitude, f.MagnitudeBn)).ToList(),
        new ApprovalRecommendationDto(i.Recommendation.Title, i.Recommendation.TitleBn, i.Recommendation.Action, i.Recommendation.ActionBn,
            StageNames.ToWire(i.Recommendation.RiskTier), StageNames.ToWire(i.Recommendation.TargetStage)),
        i.Evidence.Select(e => new EvidenceRefDto(e.Domain, e.Count, e.Filter, e.PreviewIds)).ToList(),
        StageNames.ToWire(i.Stage), i.Confidence, i.Pinned, i.CreatedAt, i.UpdatedAt);

    // ------------------------------------------------------------------
    // Shared factory plumbing
    // ------------------------------------------------------------------

    private static bool CanRead(AccessContext caller, Guid factoryId) => caller.CanAccessFactory(factoryId) && caller.Scope >= RoleScope.Line;

    private static AccessResult<T> NoFactory<T>() => AccessResult<T>.NotFound("factory_not_found", "Factory not found.");

    /// <summary>The requested factory, or the caller's busiest one (most lines) when none is given.</summary>
    private async Task<(Guid Id, string Code)?> ResolveFactoryAsync(AccessContext caller, Guid? requested, CancellationToken ct)
    {
        if (requested is { } id)
        {
            if (!CanRead(caller, id)) return null;
            var code = await _db.Factories.AsNoTracking().Where(f => f.Id == id).Select(f => f.Code).FirstOrDefaultAsync(ct);
            return code is null ? null : (id, code);
        }
        if (caller.Scope < RoleScope.Line) return null;
        var q = _db.Factories.AsNoTracking().Where(f => f.IsActive);
        if (!caller.IsPlatform)
        {
            var mine = caller.FactoryIds.ToList();
            q = q.Where(f => mine.Contains(f.Id));
        }
        // The factory with the most production lines (ties by code), so the first view isn't an empty factory.
        var first = await q.OrderByDescending(f => _db.ProductionLines.Count(l => l.FactoryId == f.Id && l.IsActive)).ThenBy(f => f.Code)
            .Select(f => new { f.Id, f.Code }).FirstOrDefaultAsync(ct);
        return first is null ? null : (first.Id, first.Code);
    }

    /// <summary>Lines of the factory the caller may see, with the floor most of their workstations stand on.</summary>
    private async Task<(List<(string Id, string Name)> Lines, Dictionary<string, Guid> LineFloor, List<WsRow> Workstations)> LayoutAsync(
        AccessContext caller, Guid factoryId, CancellationToken ct)
    {
        var lines = (await _db.ProductionLines.AsNoTracking().Where(l => l.FactoryId == factoryId && l.IsActive)
                .OrderBy(l => l.Name).Select(l => new { l.Id, l.Name }).ToListAsync(ct))
            .Where(l => !caller.IsLineScoped || caller.LineIds.Contains(l.Id))
            .Select(l => (l.Id, l.Name)).ToList();
        var lineIds = lines.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var ws = (await _db.Workstations.AsNoTracking().Where(w => w.FactoryId == factoryId && w.IsActive)
                .Select(w => new WsRow(w.Id, w.LineId, w.FloorId)).ToListAsync(ct))
            .Where(w => lineIds.Contains(w.LineId)).ToList();
        var lineFloor = ws.Where(w => w.FloorId is not null)
            .GroupBy(w => w.LineId)
            .ToDictionary(g => g.Key, g => g.GroupBy(w => w.FloorId!.Value).OrderByDescending(x => x.Count()).First().Key, StringComparer.Ordinal);
        // Workers placed on a floor tell us where a line is when its workstations don't.
        var workerFloors = await _db.Workers.AsNoTracking()
            .Where(w => w.FactoryId == factoryId && w.Status == "Active" && w.FloorId != null && w.LineId != null)
            .Select(w => new { w.LineId, w.FloorId }).ToListAsync(ct);
        foreach (var g in workerFloors.Where(w => lineIds.Contains(w.LineId!) && !lineFloor.ContainsKey(w.LineId!)).GroupBy(w => w.LineId!))
            lineFloor[g.Key] = g.GroupBy(w => w.FloorId!.Value).OrderByDescending(x => x.Count()).First().Key;
        return (lines, lineFloor, ws);
    }

    private sealed record WsRow(Guid Id, string LineId, Guid? FloorId);

    // ------------------------------------------------------------------
    // Floor status
    // ------------------------------------------------------------------

    public async Task<AccessResult<FloorStatusResponse>> FloorStatusAsync(AccessContext caller, Guid factoryId, CancellationToken ct)
    {
        var factory = await ResolveFactoryAsync(caller, factoryId, ct);
        if (factory is null) return NoFactory<FloorStatusResponse>();
        var fid = factory.Value.Id;

        var floors = await _db.ProductionFloors.AsNoTracking().Where(f => f.FactoryId == fid)
            .OrderByDescending(f => f.IsActive).ThenBy(f => f.Code).Select(f => new { f.Id, f.Code, f.Name, f.IsActive }).ToListAsync(ct);
        var (lines, lineFloor, ws) = await LayoutAsync(caller, fid, ct);
        var lineIds = lines.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var wsFloor = ws.ToDictionary(w => w.Id, w => w.FloorId ?? (lineFloor.TryGetValue(w.LineId, out var f) ? f : (Guid?)null));
        var wsLine = ws.ToDictionary(w => w.Id, w => w.LineId);

        var machines = await _db.Machines.AsNoTracking().Where(m => m.FactoryId == fid && m.IsActive && m.WorkstationId != null)
            .Select(m => new { m.Id, WorkstationId = m.WorkstationId!.Value }).ToListAsync(ct);
        machines = machines.Where(m => wsLine.ContainsKey(m.WorkstationId)).ToList();
        var states = await _db.MachineStates.AsNoTracking().Where(s => s.FactoryId == fid && s.SourceType == Real)
            .Select(s => new { s.MachineId, s.State }).ToDictionaryAsync(s => s.MachineId, s => s.State, ct);

        var cameras = await _db.Cameras.AsNoTracking().Where(c => c.FactoryId == fid && c.Status == "Active")
            .GroupBy(c => c.FloorId).Select(g => new { FloorId = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.FloorId, x => x.N, ct);

        var devices = await _db.Devices.AsNoTracking().Where(d => d.FactoryId == fid && d.Status != DeviceStatuses.Disabled && !d.IsSimulated)
            .Select(d => new { d.LineId, d.WorkstationId, d.ConnectionStatus }).ToListAsync(ct);

        var activeStatuses = AlertStatuses.Active;
        var alerts = await _db.MonitoringAlerts.AsNoTracking()
            .Where(a => a.FactoryId == fid && a.SourceType == Real && activeStatuses.Contains(a.Status))
            .OrderByDescending(a => a.LastOccurredAt)
            .Select(a => new { a.Id, a.Number, a.Title, a.Severity, a.Status, a.FloorId, a.LineId, a.WorkstationId, a.LastOccurredAt })
            .Take(500).ToListAsync(ct);

        var exceptions = await _db.ProductionExceptions.AsNoTracking()
            .Where(e => e.FactoryId == fid && e.SourceType == Real && e.Status != "RESOLVED" && e.LineId != null)
            .Select(e => new { e.LineId, e.Severity }).ToListAsync(ct);

        var workers = await _db.Workers.AsNoTracking().Where(w => w.FactoryId == fid && w.Status == "Active")
            .Select(w => new { w.Id, w.FloorId, w.LineId }).ToListAsync(ct);
        var workerFloor = workers.ToDictionary(w => w.Id, w => w.FloorId ?? (w.LineId is not null && lineFloor.TryGetValue(w.LineId, out var f) ? f : (Guid?)null));
        var openSessions = await _db.AttendanceSessions.AsNoTracking()
            .Where(s => s.FactoryId == fid && s.Status == "Open" && s.CheckOutAt == null)
            .Select(s => s.WorkerId).Distinct().ToListAsync(ct);

        Guid? FloorOfLine(string? lineId) => lineId is not null && lineFloor.TryGetValue(lineId, out var f) ? f : null;
        Guid? FloorOfAlert(Guid? floorId, string? lineId, Guid? wsId) =>
            floorId ?? (wsId is { } w && wsFloor.TryGetValue(w, out var wf) ? wf : null) ?? FloorOfLine(lineId);

        var result = new List<FloorStatusDto>();
        foreach (var floor in floors)
        {
            var floorLines = lines.Where(l => FloorOfLine(l.Id) == floor.Id).ToList();
            var floorWs = ws.Where(w => wsFloor[w.Id] == floor.Id).Select(w => w.Id).ToHashSet();
            var floorMachines = machines.Where(m => floorWs.Contains(m.WorkstationId)).ToList();
            var stateCounts = floorMachines.GroupBy(m => states.TryGetValue(m.Id, out var s) ? s : MachineStates.Unknown)
                .ToDictionary(g => g.Key, g => g.Count());
            var floorDevices = devices.Where(d => (d.WorkstationId is { } w && wsFloor.TryGetValue(w, out var wf) ? wf : FloorOfLine(d.LineId)) == floor.Id).ToList();
            var floorAlerts = alerts.Where(a => FloorOfAlert(a.FloorId, a.LineId, a.WorkstationId) == floor.Id).ToList();
            var floorLineIds = floorLines.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
            var floorExceptions = exceptions.Where(e => floorLineIds.Contains(e.LineId!)).ToList();
            var floorWorkers = workers.Where(w => workerFloor[w.Id] == floor.Id).Select(w => w.Id).ToHashSet();

            int Count(string s) => stateCounts.TryGetValue(s, out var n) ? n : 0;
            var high = floorAlerts.Count(a => AlertSeverities.Rank(a.Severity) >= 3);
            var errors = Count(MachineStates.Error);
            var stopped = Count(MachineStates.Stopped) + Count(MachineStates.Offline);
            var offline = floorDevices.Count(d => d.ConnectionStatus == ConnectionStatuses.Offline);
            var highExceptions = floorExceptions.Count(e => e.Severity == "HIGH");

            var reasons = new List<string>();
            if (high > 0) reasons.Add($"{high} high-severity alert{S(high)} open");
            if (errors > 0) reasons.Add($"{errors} machine{S(errors)} in error");
            if (highExceptions > 0) reasons.Add($"{highExceptions} high production exception{S(highExceptions)}");
            var act = reasons.Count > 0;
            var other = floorAlerts.Count - high;
            if (other > 0) reasons.Add($"{other} other alert{S(other)} open");
            if (stopped > 0) reasons.Add($"{stopped} machine{S(stopped)} stopped or offline");
            if (floorExceptions.Count - highExceptions > 0) reasons.Add($"{floorExceptions.Count - highExceptions} open production exception{S(floorExceptions.Count - highExceptions)}");
            if (offline > 0) reasons.Add($"{offline} device{S(offline)} offline");
            var status = act ? "act" : reasons.Count > 0 ? "watch" : "ok";

            result.Add(new FloorStatusDto(
                floor.Id, floor.Code, floor.Name, floor.IsActive, status, reasons,
                floorLines.Select(l =>
                {
                    var lws = ws.Where(w => w.LineId == l.Id).Select(w => w.Id).ToHashSet();
                    var lm = machines.Where(m => lws.Contains(m.WorkstationId)).ToList();
                    return new FloorLineDto(l.Id, l.Name, lws.Count, lm.Count,
                        lm.Count(m => states.TryGetValue(m.Id, out var s) && MachineStates.Downtime.Contains(s)),
                        floorAlerts.Count(a => a.LineId == l.Id));
                }).ToList(),
                floorWorkers.Count, openSessions.Count(floorWorkers.Contains), floorWs.Count, floorMachines.Count, stateCounts,
                cameras.TryGetValue(floor.Id, out var cams) ? cams : 0,
                floorDevices.Count(d => d.ConnectionStatus == ConnectionStatuses.Online), offline,
                floorAlerts.Count, high, floorExceptions.Count,
                floorAlerts.Take(5).Select(a => new FloorAlertDto(a.Id, a.Number, a.Title, a.Severity, a.Status, a.LineId, a.LastOccurredAt)).ToList()));
        }

        var unplaced = lines.Count(l => FloorOfLine(l.Id) is null);
        return AccessResult<FloorStatusResponse>.Ok(new FloorStatusResponse(fid, factory.Value.Code, DateTimeOffset.UtcNow, result, unplaced,
            "Only real (not simulated) alerts, machine states and exceptions set a floor's status. A line belongs to the floor most of its workstations stand on."));
    }

    private static string S(int n) => n == 1 ? "" : "s";

    // ------------------------------------------------------------------
    // Factory graph
    // ------------------------------------------------------------------

    public async Task<AccessResult<BrainGraphResponse>> BrainGraphAsync(AccessContext caller, Guid? factoryId, CancellationToken ct)
    {
        var factory = await ResolveFactoryAsync(caller, factoryId, ct);
        if (factory is null) return NoFactory<BrainGraphResponse>();
        var fid = factory.Value.Id;

        var nodes = new Dictionary<string, BrainNodeDto>(StringComparer.Ordinal);
        var edges = new Dictionary<string, BrainEdgeDto>(StringComparer.Ordinal);
        var omitted = new List<string>();
        var truncated = new List<string>();
        void Node(string id, string kind, string label, double weight, string? status, Dictionary<string, string> meta, string? href) =>
            nodes.TryAdd(id, new BrainNodeDto(id, kind, label, label, Math.Clamp(weight, 0, 1), status, meta, href));
        void Edge(string a, string b, string kind, double weight = 0.5, bool strong = false, string? label = null)
        {
            if (!nodes.ContainsKey(a) || !nodes.ContainsKey(b)) return;
            edges.TryAdd($"{a}>{b}", new BrainEdgeDto($"{a}>{b}", a, b, weight, strong, kind, label));
        }

        var seeProduction = caller.HasAny(Permissions.ProductionView, Permissions.PlanningView);
        var seeMachines = caller.HasAny(Permissions.MonitoringView, Permissions.DevicesView, Permissions.ProductionView);
        var seeAlerts = caller.HasAny(Permissions.MonitoringView, Permissions.ProductionView);

        // Floors and lines.
        var floors = await _db.ProductionFloors.AsNoTracking().Where(f => f.FactoryId == fid && f.IsActive)
            .Select(f => new { f.Id, f.Code, f.Name }).ToListAsync(ct);
        foreach (var f in floors)
            Node($"floor:{f.Id}", "floor", $"{f.Code} · {f.Name}", 0.9, null, new() { ["code"] = f.Code }, $"/app/floors/{f.Id}");
        var (lines, lineFloor, ws) = await LayoutAsync(caller, fid, ct);
        var wsCount = ws.GroupBy(w => w.LineId).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var l in lines)
        {
            Node($"line:{l.Id}", "line", l.Name, 0.8, null, new() { ["workstations"] = (wsCount.TryGetValue(l.Id, out var n) ? n : 0).ToString() }, $"/app/production/lines/{Uri.EscapeDataString(l.Id)}");
            if (lineFloor.TryGetValue(l.Id, out var fl)) Edge($"floor:{fl}", $"line:{l.Id}", "contains", 0.8, strong: true);
        }
        var lineIds = lines.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);

        // Machines with their derived state.
        var machineLine = new Dictionary<Guid, string>();
        if (seeMachines)
        {
            var wsLine = ws.ToDictionary(w => w.Id, w => w.LineId);
            var machines = await _db.Machines.AsNoTracking().Where(m => m.FactoryId == fid && m.IsActive)
                .OrderBy(m => m.Code).Select(m => new { m.Id, m.Code, m.MachineType, m.WorkstationId }).ToListAsync(ct);
            var states = await _db.MachineStates.AsNoTracking().Where(s => s.FactoryId == fid && s.SourceType == Real)
                .Select(s => new { s.MachineId, s.State, s.InMaintenance }).ToDictionaryAsync(s => s.MachineId, ct);
            const int maxMachines = 150;
            if (machines.Count > maxMachines) truncated.Add($"Showing {maxMachines} of {machines.Count} machines.");
            foreach (var m in machines.Take(maxMachines))
            {
                var state = states.TryGetValue(m.Id, out var s) ? s.State : null;
                var status = state switch
                {
                    MachineStates.Running => "healthy",
                    MachineStates.Error or MachineStates.Stopped or MachineStates.Offline => "down",
                    MachineStates.Idle or MachineStates.Maintenance => "at_risk",
                    _ => null,
                };
                var meta = new Dictionary<string, string> { ["state"] = state ?? "no reading" };
                if (!string.IsNullOrWhiteSpace(m.MachineType)) meta["type"] = m.MachineType!;
                Node($"machine:{m.Id}", "machine", m.Code, 0.4, status, meta, $"/app/monitoring/machines/{m.Id}");
                if (m.WorkstationId is { } w && wsLine.TryGetValue(w, out var line))
                {
                    machineLine[m.Id] = line;
                    Edge($"line:{line}", $"machine:{m.Id}", "has", 0.4);
                }
            }
        }
        else omitted.Add("Machines (needs monitoring, devices or production access).");

        // Orders, buyers, styles, targets.
        if (seeProduction)
        {
            var open = new[] { ProductionOrderStatus.Planned, ProductionOrderStatus.InProgress, ProductionOrderStatus.OnHold };
            const int maxOrders = 60;
            var orders = await _db.ProductionOrders.AsNoTracking().Where(o => o.FactoryId == fid && open.Contains(o.Status))
                .OrderBy(o => o.DueDate).Take(maxOrders + 1)
                .Select(o => new { o.Id, o.OrderNumber, o.Buyer, o.Quantity, o.DueDate, o.Status, o.StyleId, o.Priority }).ToListAsync(ct);
            if (orders.Count > maxOrders) { truncated.Add($"Showing the {maxOrders} open orders due soonest."); orders = orders.Take(maxOrders).ToList(); }
            var styleIds = orders.Select(o => o.StyleId).Distinct().ToList();
            var styles = await _db.ProductionStyles.AsNoTracking().Where(s => styleIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Code, s.Name, s.ProductType }).ToListAsync(ct);
            foreach (var s in styles)
                Node($"style:{s.Id}", "process", $"{s.Code} · {s.Name}", 0.5, null,
                    s.ProductType is null ? new() : new() { ["product"] = s.ProductType }, "/app/planning/styles");
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            foreach (var o in orders)
            {
                var late = o.DueDate < today;
                var soon = !late && o.DueDate <= today.AddDays(7);
                Node($"order:{o.Id}", "order", o.OrderNumber, 0.6, late ? "down" : o.Status == ProductionOrderStatus.OnHold || soon ? "at_risk" : "healthy",
                    new() { ["quantity"] = o.Quantity.ToString(), ["due"] = o.DueDate.ToString("yyyy-MM-dd"), ["status"] = o.Status.ToString(), ["priority"] = o.Priority },
                    $"/app/planning/orders/{o.Id}");
                Edge($"order:{o.Id}", $"style:{o.StyleId}", "follows", 0.4);
                if (!string.IsNullOrWhiteSpace(o.Buyer))
                {
                    var key = o.Buyer!.Trim().ToLowerInvariant();
                    Node($"buyer:{key}", "buyer", o.Buyer.Trim(), 0.7, null, new(), $"/app/planning/orders?q={Uri.EscapeDataString(o.Buyer.Trim())}");
                    Edge($"buyer:{key}", $"order:{o.Id}", "shipped_to", 0.6);
                }
            }
            var orderIds = orders.Select(o => o.Id).ToList();
            var allocations = await _db.ProductionAllocations.AsNoTracking()
                .Where(a => a.FactoryId == fid && orderIds.Contains(a.OrderId) && a.Status != "CANCELLED")
                .Select(a => new { a.OrderId, a.LineId, a.Quantity }).ToListAsync(ct);
            foreach (var a in allocations) Edge($"order:{a.OrderId}", $"line:{a.LineId}", "assigned_to", 0.7, strong: true, label: $"{a.Quantity} pcs");

            var targets = await _db.ProductionTargets.AsNoTracking()
                .Where(t => t.FactoryId == fid && !t.IsSuperseded && t.LineId != null && t.EffectiveFrom <= today && (t.EffectiveTo == null || t.EffectiveTo >= today)
                    && t.ApprovalStatus != "REJECTED" && t.ApprovalStatus != "PENDING")
                .OrderBy(t => t.LineId).Take(80)
                .Select(t => new { t.Id, t.LineId, t.Quantity, t.Period, t.Level }).ToListAsync(ct);
            foreach (var t in targets.Where(t => lineIds.Contains(t.LineId!)))
            {
                Node($"target:{t.Id}", "target", $"{t.Quantity} pcs / {t.Period.ToLowerInvariant()}", 0.4, null,
                    new() { ["level"] = t.Level, ["period"] = t.Period }, "/app/planning/targets");
                Edge($"target:{t.Id}", $"line:{t.LineId}", "has", 0.4);
            }
        }
        else omitted.Add("Orders, buyers, styles and targets (needs production or planning access).");

        // Risks: active real alerts and open production exceptions.
        if (seeAlerts)
        {
            var activeStatuses = AlertStatuses.Active;
            var alerts = await _db.MonitoringAlerts.AsNoTracking()
                .Where(a => a.FactoryId == fid && a.SourceType == Real && activeStatuses.Contains(a.Status))
                .OrderByDescending(a => a.LastOccurredAt).Take(40)
                .Select(a => new { a.Id, a.Number, a.Title, a.Severity, a.Status, a.FloorId, a.LineId, a.MachineId }).ToListAsync(ct);
            foreach (var a in alerts)
            {
                var id = $"alert:{a.Id}";
                Node(id, "risk", a.Title, AlertSeverities.Rank(a.Severity) / 4.0, AlertSeverities.Rank(a.Severity) >= 3 ? "down" : "at_risk",
                    new() { ["alert"] = $"#{a.Number}", ["severity"] = a.Severity, ["status"] = a.Status }, $"/app/monitoring/alerts/{a.Id}");
                if (a.MachineId is { } m && nodes.ContainsKey($"machine:{m}")) Edge(id, $"machine:{m}", "threatens", 0.8, strong: true);
                else if (a.LineId is not null) Edge(id, $"line:{a.LineId}", "threatens", 0.8, strong: true);
                else if (a.FloorId is { } f) Edge(id, $"floor:{f}", "threatens", 0.8, strong: true);
            }
            if (seeProduction)
            {
                var exceptions = await _db.ProductionExceptions.AsNoTracking()
                    .Where(e => e.FactoryId == fid && e.SourceType == Real && e.Status != "RESOLVED")
                    .OrderByDescending(e => e.LastDetectedAt).Take(40)
                    .Select(e => new { e.Id, e.Title, e.Severity, e.Status, e.Type, e.LineId, e.OrderId }).ToListAsync(ct);
                foreach (var e in exceptions)
                {
                    var id = $"exception:{e.Id}";
                    Node(id, "risk", e.Title, e.Severity == "HIGH" ? 0.75 : e.Severity == "MEDIUM" ? 0.5 : 0.25, e.Severity == "HIGH" ? "down" : "at_risk",
                        new() { ["type"] = e.Type, ["severity"] = e.Severity, ["status"] = e.Status }, "/app/planning/exceptions");
                    if (e.OrderId is { } o) Edge(id, $"order:{o}", "threatens", 0.7, strong: true);
                    if (e.LineId is not null) Edge(id, $"line:{e.LineId}", "threatens", 0.7, strong: true);
                }
            }
            // A line with a down machine or a high-severity risk is flagged on the line node too.
            foreach (var l in lines)
            {
                var lid = $"line:{l.Id}";
                var linked = edges.Values.Where(e => e.Target == lid || e.Source == lid).Select(e => e.Source == lid ? e.Target : e.Source)
                    .Select(id => nodes[id]).Where(n => n.Kind is "machine" or "risk").ToList();
                var status = linked.Any(n => n.Kind == "risk" && n.Status == "down") ? "down"
                    : linked.Any(n => n.Status is "down" or "at_risk") ? "at_risk"
                    : linked.Any(n => n.Status == "healthy") ? "healthy" : null;
                if (status is not null) nodes[lid] = nodes[lid] with { Status = status };
            }
        }
        else omitted.Add("Alerts and exceptions (needs monitoring or production access).");

        return AccessResult<BrainGraphResponse>.Ok(new BrainGraphResponse(fid, factory.Value.Code, DateTimeOffset.UtcNow,
            nodes.Values.ToList(), edges.Values.ToList(), omitted, truncated));
    }

    // ------------------------------------------------------------------
    // Integrations
    // ------------------------------------------------------------------

    public async Task<AccessResult<IntegrationStatusResponse>> IntegrationsAsync(AccessContext caller, Guid? factoryId, CancellationToken ct)
    {
        var factory = await ResolveFactoryAsync(caller, factoryId, ct);
        if (factory is null) return NoFactory<IntegrationStatusResponse>();
        var fid = factory.Value.Id;
        var since = DateTimeOffset.UtcNow - Recent;

        var devices = await _db.Devices.AsNoTracking().Where(d => d.FactoryId == fid && d.Status != DeviceStatuses.Disabled)
            .Select(d => new { d.Category, d.ConnectionStatus, d.IsSimulated }).ToListAsync(ct);
        (int Total, int Online, int Offline) Dev(params string[] categories)
        {
            var list = devices.Where(d => categories.Contains(d.Category)).ToList();
            return (list.Count, list.Count(d => d.ConnectionStatus == ConnectionStatuses.Online), list.Count(d => d.ConnectionStatus == ConnectionStatuses.Offline));
        }

        var list = new List<IntegrationStatusDto>();
        IntegrationStatusDto Build(string id, string category, string label, string description, (int Total, int Online, int Offline) dev,
            long records, long recent, long simulated, DateTimeOffset? last, string href, string? note = null)
        {
            var status = recent > 0 ? "receiving" : dev.Total > 0 || records > 0 ? "connected" : "not_connected";
            return new IntegrationStatusDto(id, category, label, description, status, dev.Total, dev.Online, dev.Offline, records, recent, simulated, last, href, note);
        }

        // RFID readers (bundle / worker tags).
        {
            var q = _db.RfidReadEvents.AsNoTracking().Where(e => e.FactoryId == fid);
            list.Add(Build("rfid", "sensor", "RFID readers", "Tag reads from RFID readers (worker cards, bundles).", Dev(DeviceCategories.RfidReader),
                await q.LongCountAsync(ct), await q.LongCountAsync(e => e.ReceivedAt >= since, ct), await q.LongCountAsync(e => e.IsSimulated, ct),
                await q.MaxAsync(e => (DateTimeOffset?)e.ReceivedAt, ct), "/app/cameras-devices/devices?category=RfidReader"));
        }
        // Attendance punches from readers.
        {
            var q = _db.AttendanceEvents.AsNoTracking().Where(e => e.FactoryId == fid && e.Source == "Device");
            var readers = await _db.AttendanceReaders.AsNoTracking().CountAsync(r => r.FactoryId == fid && r.IsActive && !r.IsSimulated, ct);
            list.Add(Build("attendance", "sensor", "Attendance card readers", "Check-in and check-out punches recorded by card readers.", (readers, 0, 0),
                await q.LongCountAsync(ct), await q.LongCountAsync(e => e.ReceivedAt >= since, ct), await q.LongCountAsync(e => e.IsSimulated, ct),
                await q.MaxAsync(e => (DateTimeOffset?)e.ReceivedAt, ct), "/app/attendance", "Reader counts are attendance readers; online state isn't tracked for them."));
        }
        // Machine sensors (telemetry).
        {
            var q = _db.MachineTelemetry.AsNoTracking().Where(e => e.FactoryId == fid);
            list.Add(Build("machine_telemetry", "sensor", "Machine sensors", "Run / stop, power, temperature and vibration readings from machine sensors.", Dev(DeviceCategories.MachineSensor),
                await q.LongCountAsync(ct), await q.LongCountAsync(e => e.ReceivedAt >= since, ct), await q.LongCountAsync(e => e.SourceType != Real, ct),
                await q.MaxAsync(e => (DateTimeOffset?)e.ReceivedAt, ct), "/app/monitoring/machines"));
        }
        // Production counters.
        {
            var q = _db.ProductionCounterEvents.AsNoTracking().Where(e => e.FactoryId == fid);
            list.Add(Build("production_counters", "sensor", "Production counters", "Piece counts sent by counters on the line.", Dev(DeviceCategories.ProductionCounter),
                await q.LongCountAsync(ct), await q.LongCountAsync(e => e.ReceivedAt >= since, ct), await q.LongCountAsync(e => e.SourceType != Real, ct),
                await q.MaxAsync(e => (DateTimeOffset?)e.ReceivedAt, ct), "/app/production"));
        }
        // Gateways.
        {
            var dev = Dev(DeviceCategories.Esp32Gateway, DeviceCategories.IndustrialGateway);
            list.Add(Build("gateways", "sensor", "IoT gateways", "ESP32 and industrial gateways that forward sensor data.", dev, 0, 0, 0, null,
                "/app/cameras-devices/devices", dev.Total == 0 ? null : $"{dev.Online} online, {dev.Offline} offline."));
        }
        // Cameras and camera AI.
        {
            var cams = await _db.Cameras.AsNoTracking().Where(c => c.FactoryId == fid && c.Status != "Inactive").CountAsync(ct);
            var q = _db.MonitoringEvents.AsNoTracking().Where(e => e.FactoryId == fid);
            var camDev = Dev(DeviceCategories.Camera);
            list.Add(Build("cameras", "camera", "Cameras and camera AI", "Camera observations analysed by the vision service or camera AI.", (Math.Max(cams, camDev.Total), camDev.Online, camDev.Offline),
                await q.LongCountAsync(ct), await q.LongCountAsync(e => e.ReceivedAt >= since, ct), await q.LongCountAsync(e => e.SourceType != Real, ct),
                await q.MaxAsync(e => (DateTimeOffset?)e.ReceivedAt, ct), "/app/monitoring/cameras"));
        }
        // Documents (forms, uploads, imports).
        {
            var q = _db.FactoryDocuments.AsNoTracking().Where(d => d.FactoryId == fid);
            list.Add(Build("documents", "pilot", "Documents and forms", "Factory documents created from forms, uploads and imports.", (0, 0, 0),
                await q.LongCountAsync(ct), await q.LongCountAsync(d => d.CreatedAt >= since, ct), 0,
                await q.MaxAsync(d => (DateTimeOffset?)d.CreatedAt, ct), "/app/documents"));
        }
        // Production output: manual entry, CSV import, integration.
        {
            var factoryLines = await _db.ProductionLines.AsNoTracking().Where(l => l.FactoryId == fid).Select(l => l.Id).ToListAsync(ct);
            var sinceUtc = since.UtcDateTime;
            foreach (var (src, id, label, description) in new[]
            {
                (ProductionDataSource.Import, "production_import", "Production CSV import", "Output rows imported from CSV files."),
                (ProductionDataSource.Integration, "production_integration", "Production integration", "Output pushed by another system through the API."),
            })
            {
                var q = _db.ProductionOutputEvents.AsNoTracking().Where(e => factoryLines.Contains(e.LineId) && e.Source == src);
                var last = await q.MaxAsync(e => (DateTime?)e.RecordedAt, ct);
                list.Add(Build(id, "pilot", label, description, (0, 0, 0),
                    await q.LongCountAsync(ct), await q.LongCountAsync(e => e.RecordedAt >= sinceUtc, ct), 0,
                    last is null ? null : new DateTimeOffset(DateTime.SpecifyKind(last.Value, DateTimeKind.Utc)), "/app/production"));
            }
        }

        return AccessResult<IntegrationStatusResponse>.Ok(new IntegrationStatusResponse(fid, factory.Value.Code, DateTimeOffset.UtcNow, list));
    }
}
