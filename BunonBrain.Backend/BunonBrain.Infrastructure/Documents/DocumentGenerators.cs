using System.Globalization;
using System.Text.Json.Nodes;
using BunonBrain.Application.Attendance;
using BunonBrain.Application.Documents;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Attendance;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Planning;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Documents;

/// <summary>What a generator produced: the content, data gaps it found, and whether any source record existed at all.</summary>
public sealed record GeneratedContent(JsonObject Data, List<DocumentIssue> Gaps, bool HasSourceData);

/// <summary>
/// Builds report content from the operational records, deterministically:
/// production output, attendance sessions and shift assignments, overtime,
/// QC inspections, the stock ledger, machine state history and device events.
/// Figures are copied or summed, never estimated: where a record lacks a
/// value (operators not recorded, an inspection without a line), the gap is
/// reported and the value stays empty.
/// </summary>
public sealed class DocumentGenerators
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly BunonBrainDbContext _db;

    public DocumentGenerators(BunonBrainDbContext db) { _db = db; }

    public static bool Supports(string code) => DocumentCatalog.Get(code)?.Generated == true;

    public async Task<GeneratedContent> GenerateAsync(DocumentTypeDef type, Guid factoryId, DateOnly from, DateOnly to, string? lineId, JsonObject? parameters, CancellationToken ct)
    {
        var clock = await FactoryClock.LoadAsync(_db, factoryId, ct);
        return type.Code switch
        {
            "production.daily" => await DailyAsync(factoryId, from, lineId, ct),
            "production.line" => await LineAsync(factoryId, from, lineId, ct),
            "production.cutting" => await StagesAsync(factoryId, from, lineId, new[] { ProductionStage.Cutting }, ct),
            "production.sewing" => await StagesAsync(factoryId, from, lineId, new[] { ProductionStage.SewingInput, ProductionStage.SewingOutput }, ct),
            "production.finishing" => await StagesAsync(factoryId, from, lineId, new[] { ProductionStage.Finishing, ProductionStage.Packing }, ct),
            "production.wip" => await WipAsync(factoryId, to, lineId, ct),
            "production.downtime" => await DowntimeAsync(factoryId, from, lineId, clock, ct),
            "hr.attendance" => await AttendanceAsync(factoryId, from, lineId, clock, exceptionsOnly: false, ct),
            "hr.late_absent" => await AttendanceAsync(factoryId, from, lineId, clock, exceptionsOnly: true, ct),
            "hr.overtime" => await OvertimeAsync(factoryId, from, to, lineId, ct),
            "hr.payroll_input" => await PayrollAsync(factoryId, from, to, ct),
            "store.stock_register" => await RegisterAsync(factoryId, from, to, DocJson.Str(parameters?["itemCategory"]) ?? "ALL", ct),
            "store.stock_balance" => await BalanceAsync(factoryId, to, ct),
            "qc.inline" or "qc.endline" or "qc.final" => await QcStageAsync(factoryId, from, lineId, DocumentCatalog.QcStage(type.Code)!, clock, ct),
            "qc.defect" => await DefectsAsync(factoryId, from, to, lineId, clock, ct),
            "qc.rework" => await ReworkAsync(factoryId, from, to, lineId, clock, ct),
            "devices.log" => await DeviceLogAsync(factoryId, from, to, clock, ct),
            _ => throw new InvalidOperationException($"No generator for {type.Code}."),
        };
    }

    // ── Production ──────────────────────────────────────────────────────────

    private sealed record Output(string LineId, string LineName, Guid OrderId, string Order, string? Buyer, ProductionStage Stage, int Qty, int Rework, int Reject,
        int? Operators, double? Downtime, string? DowntimeReason, DateTime Start, DateTime End);

    private async Task<List<Output>> OutputsAsync(Guid factoryId, DateOnly fromDate, DateOnly toDate, string? lineId, CancellationToken ct) =>
        await (from e in _db.ProductionOutputEvents.AsNoTracking()
               join l in _db.ProductionLines.AsNoTracking() on e.LineId equals l.Id
               join o in _db.ProductionOrders.AsNoTracking() on e.OrderId equals o.Id
               where l.FactoryId == factoryId && !e.IsSuperseded && e.ProductionDate >= fromDate && e.ProductionDate <= toDate && (lineId == null || e.LineId == lineId)
               select new Output(e.LineId, l.Name, o.Id, o.OrderNumber, o.Buyer, e.Stage, e.Quantity, e.ReworkQuantity, e.RejectQuantity, e.OperatorsPresent,
                   e.DowntimeMinutes, e.DowntimeReason, e.IntervalStartUtc, e.IntervalEndUtc)).ToListAsync(ct);

    private static string StageKey(ProductionStage s) => s switch
    {
        ProductionStage.Cutting => "cutting", ProductionStage.SewingInput => "sewingInput", ProductionStage.SewingOutput => "sewingOutput",
        ProductionStage.Finishing => "finishing", _ => "packing",
    };

    private async Task<GeneratedContent> DailyAsync(Guid factoryId, DateOnly date, string? lineId, CancellationToken ct)
    {
        var rows = await OutputsAsync(factoryId, date, date, lineId, ct);
        var gaps = new List<DocumentIssue>();
        var d = new JsonObject();
        foreach (var s in DocumentCatalog.Stages) d[s] = rows.Where(r => StageKey(r.Stage) == s).Sum(r => r.Qty);
        d["rework"] = rows.Sum(r => r.Rework);
        d["reject"] = rows.Sum(r => r.Reject);
        d["downtimeMinutes"] = Math.Round(rows.Sum(r => r.Downtime ?? 0), 1);
        var lines = new JsonArray();
        foreach (var g in rows.GroupBy(r => (r.LineId, r.LineName)).OrderBy(g => g.Key.LineName))
        {
            var row = new JsonObject { ["line"] = g.Key.LineName };
            foreach (var s in DocumentCatalog.Stages) row[s] = g.Where(r => StageKey(r.Stage) == s).Sum(r => r.Qty);
            row["rework"] = g.Sum(r => r.Rework);
            row["reject"] = g.Sum(r => r.Reject);
            var ops = g.Where(r => r.Stage == ProductionStage.SewingOutput && r.Operators is not null).Select(r => r.Operators!.Value).ToList();
            if (ops.Count > 0) row["operators"] = Math.Round(ops.Average(), 1);
            else if (g.Any(r => r.Stage == ProductionStage.SewingOutput))
                gaps.Add(new DocumentIssue("lines", "operators_not_recorded", $"{g.Key.LineName}: operators present were not recorded with the sewing output; efficiency can't be judged."));
            row["downtimeMinutes"] = Math.Round(g.Sum(r => r.Downtime ?? 0), 1);
            lines.Add(row);
        }
        d["lines"] = lines;
        if (rows.Count == 0) gaps.Add(new DocumentIssue("lines", "no_output_recorded", $"No production output is recorded for {date:yyyy-MM-dd}."));
        return new GeneratedContent(d, gaps, rows.Count > 0);
    }

    private async Task<GeneratedContent> LineAsync(Guid factoryId, DateOnly date, string? lineId, CancellationToken ct)
    {
        var gaps = new List<DocumentIssue>();
        if (lineId is null) { gaps.Add(new DocumentIssue("line", "line_required", "Choose the production line.")); return new GeneratedContent(new JsonObject(), gaps, false); }
        var rows = await OutputsAsync(factoryId, date, date, lineId, ct);
        var d = new JsonObject
        {
            ["total"] = rows.Where(r => r.Stage == ProductionStage.SewingOutput).Sum(r => r.Qty),
            ["rework"] = rows.Sum(r => r.Rework), ["reject"] = rows.Sum(r => r.Reject), ["downtimeMinutes"] = Math.Round(rows.Sum(r => r.Downtime ?? 0), 1),
        };
        var arr = new JsonArray();
        foreach (var g in rows.GroupBy(r => (r.Order, r.Buyer, r.Stage)).OrderBy(g => g.Key.Order).ThenBy(g => g.Key.Stage))
        {
            var row = new JsonObject { ["order"] = g.Key.Order, ["buyer"] = g.Key.Buyer, ["stage"] = DocumentCatalog.StageLabel(StageKey(g.Key.Stage)),
                ["quantity"] = g.Sum(r => r.Qty), ["rework"] = g.Sum(r => r.Rework), ["reject"] = g.Sum(r => r.Reject) };
            var ops = g.Where(r => r.Operators is not null).Select(r => r.Operators!.Value).ToList();
            if (ops.Count > 0) row["operators"] = Math.Round(ops.Average(), 1);
            arr.Add(row);
        }
        d["rows"] = arr;
        if (rows.Count == 0) gaps.Add(new DocumentIssue("rows", "no_output_recorded", $"No output is recorded for this line on {date:yyyy-MM-dd}."));
        else if (!rows.Any(r => r.Stage == ProductionStage.SewingOutput)) gaps.Add(new DocumentIssue("total", "no_sewing_output", "No sewing output is recorded: good pieces are counted at sewing output."));
        return new GeneratedContent(d, gaps, rows.Count > 0);
    }

    private async Task<GeneratedContent> StagesAsync(Guid factoryId, DateOnly date, string? lineId, ProductionStage[] stages, CancellationToken ct)
    {
        var rows = (await OutputsAsync(factoryId, date, date, lineId, ct)).Where(r => stages.Contains(r.Stage)).ToList();
        var gaps = new List<DocumentIssue>();
        var d = new JsonObject();
        foreach (var s in stages) d[StageKey(s)] = rows.Where(r => r.Stage == s).Sum(r => r.Qty);
        d["rework"] = rows.Sum(r => r.Rework);
        d["reject"] = rows.Sum(r => r.Reject);
        var arr = new JsonArray();
        foreach (var g in rows.GroupBy(r => (r.LineName, r.Order, r.Buyer)).OrderBy(g => g.Key.LineName).ThenBy(g => g.Key.Order))
        {
            var row = new JsonObject { ["line"] = g.Key.LineName, ["order"] = g.Key.Order, ["buyer"] = g.Key.Buyer };
            foreach (var s in stages) row[StageKey(s)] = g.Where(r => r.Stage == s).Sum(r => r.Qty);
            row["rework"] = g.Sum(r => r.Rework);
            row["reject"] = g.Sum(r => r.Reject);
            arr.Add(row);
        }
        d["rows"] = arr;
        if (rows.Count == 0) gaps.Add(new DocumentIssue("rows", "no_output_recorded", $"No {string.Join(" / ", stages.Select(s => DocumentCatalog.StageLabel(StageKey(s)).ToLowerInvariant()))} output is recorded for {date:yyyy-MM-dd}."));
        return new GeneratedContent(d, gaps, rows.Count > 0);
    }

    private async Task<GeneratedContent> WipAsync(Guid factoryId, DateOnly asOf, string? lineId, CancellationToken ct)
    {
        var rows = await OutputsAsync(factoryId, DateOnly.MinValue, asOf, lineId, ct);
        var gaps = new List<DocumentIssue>();
        var orderIds = rows.Select(r => r.OrderId).Distinct().ToList();
        var orders = await _db.ProductionOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        var arr = new JsonArray();
        int cw = 0, sw = 0, fw = 0;
        foreach (var g in rows.GroupBy(r => r.OrderId))
        {
            var o = orders[g.Key];
            if (o.Status is ProductionOrderStatus.Cancelled or ProductionOrderStatus.Completed) continue;
            int Sum(ProductionStage s) => g.Where(r => r.Stage == s).Sum(r => r.Qty);
            var cut = Sum(ProductionStage.Cutting); var si = Sum(ProductionStage.SewingInput); var so = Sum(ProductionStage.SewingOutput); var fin = Sum(ProductionStage.Finishing);
            var row = new JsonObject
            {
                ["order"] = o.OrderNumber, ["buyer"] = o.Buyer, ["orderQty"] = o.Quantity, ["cut"] = cut, ["sewingInput"] = si, ["sewingOutput"] = so, ["finishing"] = fin,
                ["cuttingWip"] = cut - si, ["sewingWip"] = si - so, ["finishingWip"] = so - fin,
            };
            if (cut - si < 0 || si - so < 0 || so - fin < 0)
                gaps.Add(new DocumentIssue("orders", "negative_wip", $"{o.OrderNumber}: a later stage counts more pieces than the stage before it; some output is missing or recorded on the wrong stage."));
            cw += Math.Max(0, cut - si); sw += Math.Max(0, si - so); fw += Math.Max(0, so - fin);
            arr.Add(row);
        }
        var d = new JsonObject { ["cuttingWip"] = cw, ["sewingWip"] = sw, ["finishingWip"] = fw, ["orders"] = arr };
        if (arr.Count == 0) gaps.Add(new DocumentIssue("orders", "no_open_orders", "No open order has recorded output up to this date."));
        return new GeneratedContent(d, gaps, arr.Count > 0);
    }

    private static readonly string[] StoppedStates = { "STOPPED", "ERROR", "MAINTENANCE", "OFFLINE" };

    private async Task<GeneratedContent> DowntimeAsync(Guid factoryId, DateOnly date, string? lineId, FactoryClock clock, CancellationToken ct)
    {
        var gaps = new List<DocumentIssue>();
        var outputs = (await OutputsAsync(factoryId, date, date, lineId, ct)).Where(r => r.Downtime is > 0).ToList();
        var lineRows = new JsonArray();
        foreach (var r in outputs.OrderBy(r => r.Start))
            lineRows.Add(new JsonObject
            {
                ["line"] = r.LineName, ["order"] = r.Order, ["from"] = Local(r.Start, clock), ["to"] = Local(r.End, clock),
                ["minutes"] = Math.Round(r.Downtime!.Value, 1), ["reason"] = r.DowntimeReason ?? "(no reason recorded)",
            });
        if (outputs.Any(r => r.DowntimeReason is null)) gaps.Add(new DocumentIssue("lineDowntime", "reason_missing", "Some downtime was recorded without a reason."));

        var (start, end) = clock.DayWindow(date);
        var lookBack = start.AddDays(-7);
        var transitions = await _db.MachineStateTransitions.AsNoTracking()
            .Where(t => t.FactoryId == factoryId && t.SourceType == "REAL" && t.OccurredAt >= lookBack && t.OccurredAt < end && (lineId == null || t.LineId == lineId))
            .OrderBy(t => t.MachineId).ThenBy(t => t.OccurredAt).ToListAsync(ct);
        var machineIds = transitions.Select(t => t.MachineId).Distinct().ToList();
        var codes = await _db.Machines.AsNoTracking().Where(m => machineIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Code, ct);
        var lineNames = await _db.ProductionLines.AsNoTracking().Where(l => l.FactoryId == factoryId).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var machineRows = new JsonArray();
        double machineMinutes = 0;
        foreach (var g in transitions.GroupBy(t => t.MachineId))
        {
            var list = g.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var t = list[i];
                if (!StoppedStates.Contains(t.ToState)) continue;
                var s = t.OccurredAt < start ? start : t.OccurredAt;
                var e = i + 1 < list.Count ? list[i + 1].OccurredAt : (end < DateTimeOffset.UtcNow ? end : DateTimeOffset.UtcNow);
                if (e > end) e = end;
                if (e <= s) continue;
                var minutes = Math.Round((e - s).TotalMinutes, 1);
                machineMinutes += minutes;
                machineRows.Add(new JsonObject
                {
                    ["machine"] = codes.GetValueOrDefault(t.MachineId, t.MachineId.ToString()[..8]), ["line"] = t.LineId is null ? null : lineNames.GetValueOrDefault(t.LineId, t.LineId),
                    ["state"] = t.ToState, ["from"] = Local(s, clock), ["to"] = Local(e, clock), ["minutes"] = minutes, ["reason"] = t.Reason,
                });
            }
        }
        var d = new JsonObject
        {
            ["lineDowntimeMinutes"] = Math.Round(outputs.Sum(r => r.Downtime!.Value), 1), ["machineDowntimeMinutes"] = Math.Round(machineMinutes, 1),
            ["lineDowntime"] = lineRows, ["machineDowntime"] = machineRows,
        };
        var any = lineRows.Count > 0 || machineRows.Count > 0;
        if (!any) gaps.Add(new DocumentIssue("lineDowntime", "no_downtime_recorded", "No downtime and no machine stoppage is recorded for this day (machine data from REAL sensors only)."));
        return new GeneratedContent(d, gaps, any);
    }

    private static string Local(DateTime utc, FactoryClock clock) => Local(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)), clock);
    private static string Local(DateTimeOffset utc, FactoryClock clock) => TimeZoneInfo.ConvertTime(utc, clock.Zone).ToString("yyyy-MM-dd HH:mm", Inv);

    // ── HR ──────────────────────────────────────────────────────────────────

    private async Task<GeneratedContent> AttendanceAsync(Guid factoryId, DateOnly date, string? lineId, FactoryClock clock, bool exceptionsOnly, CancellationToken ct)
    {
        var gaps = new List<DocumentIssue>();
        var sessions = await (from s in _db.AttendanceSessions.AsNoTracking()
                              join w in _db.Workers.AsNoTracking() on s.WorkerId equals w.Id
                              where s.FactoryId == factoryId && s.WorkDate == date && (lineId == null || s.LineId == lineId)
                              select new { s, w.WorkerCode, w.FullName }).ToListAsync(ct);
        var lineNames = await _db.ProductionLines.AsNoTracking().Where(l => l.FactoryId == factoryId).ToDictionaryAsync(l => l.Id, l => l.Name, ct);

        // Scheduled workers: an active shift assignment covering the date on one of the shift's working days.
        var scheduled = await (from a in _db.ShiftAssignments.AsNoTracking()
                               join sh in _db.Shifts.AsNoTracking() on a.ShiftId equals sh.Id
                               join w in _db.Workers.AsNoTracking() on a.WorkerId equals w.Id
                               where a.FactoryId == factoryId && a.Status != "Superseded" && a.EffectiveFrom <= date && (a.EffectiveTo == null || a.EffectiveTo >= date)
                                     && w.Status == "Active" && (lineId == null || w.LineId == lineId)
                               select new { w.Id, w.WorkerCode, w.FullName, w.LineId, sh.WorkingDays, sh.Name }).ToListAsync(ct);
        var rostered = scheduled.Where(s => AttendanceRules.WorksOn(s.WorkingDays, date.DayOfWeek)).GroupBy(s => s.Id).Select(g => g.First()).ToList();
        var presentIds = sessions.Where(x => x.s.CheckInAt != null).Select(x => x.s.WorkerId).ToHashSet();
        // Holidays and approved leave: those workers are not expected, so they are never absent.
        var calendar = await AttendanceCalendarLoader.LoadAsync(_db, new[] { factoryId }, date, date, ct);
        var holiday = calendar.HolidayOn(factoryId, date);
        var (scheduledToday, excused) = calendar.Apply(rostered, s => s.Id, _ => date, _ => factoryId);
        var onLeave = excused.Where(e => e.Kind == ExcusalKinds.Leave && !presentIds.Contains(e.WorkerId)).ToList();
        var workedOnLeave = excused.Where(e => e.Kind == ExcusalKinds.Leave && presentIds.Contains(e.WorkerId)).ToList();
        var absent = scheduledToday.Where(s => !presentIds.Contains(s.Id)).ToList();
        var today = clock.Today(DateTimeOffset.UtcNow);

        var d = new JsonObject();
        var rows = new JsonArray();
        string LineName(string? id) => id is null ? "" : lineNames.GetValueOrDefault(id, id);
        string? Time(DateTimeOffset? t) => t is null ? null : TimeZoneInfo.ConvertTime(t.Value, clock.Zone).ToString("HH:mm", Inv);
        int late = 0, early = 0, missing = 0;
        foreach (var x in sessions.OrderBy(x => x.WorkerCode))
        {
            var s = x.s;
            var isMissing = s.Status is "MissingCheckOut" or "MissingCheckIn";
            if (s.IsLate) late++;
            if (s.IsEarlyDeparture) early++;
            if (isMissing) missing++;
            if (!exceptionsOnly)
                rows.Add(new JsonObject
                {
                    ["workerCode"] = x.WorkerCode, ["name"] = x.FullName, ["line"] = LineName(s.LineId), ["checkIn"] = Time(s.CheckInAt), ["checkOut"] = Time(s.CheckOutAt),
                    ["netMinutes"] = s.NetWorkingMinutes, ["lateMinutes"] = s.LateMinutes, ["status"] = s.Status + (s.HasSimulatedEvents ? " (simulated)" : ""),
                });
            else
            {
                if (s.IsLate) rows.Add(Exc(x.WorkerCode, x.FullName, LineName(s.LineId), "LATE", s.LateMinutes));
                if (s.IsEarlyDeparture) rows.Add(Exc(x.WorkerCode, x.FullName, LineName(s.LineId), "EARLY_DEPARTURE", s.EarlyDepartureMinutes));
                if (isMissing) rows.Add(Exc(x.WorkerCode, x.FullName, LineName(s.LineId), s.Status == "MissingCheckIn" ? "MISSING_CHECK_IN" : "MISSING_CHECK_OUT", null));
            }
        }
        if (!exceptionsOnly)
            foreach (var l in onLeave.Select(e => (e, w: rostered.First(r => r.Id == e.WorkerId))).OrderBy(x => x.w.WorkerCode))
                rows.Add(new JsonObject { ["workerCode"] = l.w.WorkerCode, ["name"] = l.w.FullName, ["line"] = LineName(l.w.LineId), ["status"] = $"ON LEAVE ({l.e.Detail}, {l.e.Reference})" });
        // Absence is only known once the day is over (or nobody can still check in).
        var dayOver = date < today;
        if (dayOver)
            foreach (var a in absent.OrderBy(a => a.WorkerCode))
                rows.Add(exceptionsOnly ? Exc(a.WorkerCode, a.FullName, LineName(a.LineId), "ABSENT", null)
                    : new JsonObject { ["workerCode"] = a.WorkerCode, ["name"] = a.FullName, ["line"] = LineName(a.LineId), ["status"] = "ABSENT" });
        else if (absent.Count > 0)
            gaps.Add(new DocumentIssue("absent", "day_not_over", "The day isn't over: scheduled workers without a check-in are not counted absent yet."));
        if (exceptionsOnly)
        {
            d["late"] = late; d["absent"] = dayOver ? absent.Count : 0; d["onLeave"] = onLeave.Count; d["earlyDeparture"] = early; d["missingPunch"] = missing;
        }
        else
        {
            d["scheduled"] = scheduledToday.Count; d["present"] = presentIds.Count; d["absent"] = dayOver ? absent.Count : 0; d["onLeave"] = onLeave.Count; d["late"] = late;
            d["earlyDeparture"] = early; d["missingPunch"] = missing; d["netHours"] = Math.Round(sessions.Sum(x => x.s.NetWorkingMinutes) / 60.0, 2);
            if (holiday is not null) d["holiday"] = $"{holiday.Name} ({holiday.Kind.ToLowerInvariant()})";
        }
        d["rows"] = rows;
        if (holiday is not null)
            gaps.Add(new DocumentIssue("holiday", "holiday", $"{date:yyyy-MM-dd} is a holiday ({holiday.Name}): nobody was expected, so nobody is absent{(presentIds.Count > 0 ? $"; {presentIds.Count} worked" : "")}."));
        else if (rostered.Count == 0) gaps.Add(new DocumentIssue("scheduled", "no_shift_assignments", "No worker has a shift assignment for this day, so absence can't be determined."));
        if (workedOnLeave.Count > 0)
            gaps.Add(new DocumentIssue("onLeave", "worked_during_leave", $"{workedOnLeave.Count} worker(s) on approved leave checked in ({string.Join(", ", workedOnLeave.Select(e => e.Reference).Distinct().Take(5))}): counted present; HR should check the leave record."));
        if (missing > 0) gaps.Add(new DocumentIssue("missingPunch", "missing_punches", $"{missing} session(s) have a missing punch: HR must correct them before the hours are final."));
        if (sessions.Any(x => x.s.HasSimulatedEvents)) gaps.Add(new DocumentIssue("rows", "simulated_events", "Some sessions include SIMULATED punches (RFID simulator)."));
        return new GeneratedContent(d, gaps, sessions.Count > 0 || (dayOver && rostered.Count > 0));
    }

    private static JsonObject Exc(string code, string name, string line, string kind, int? minutes)
    {
        var o = new JsonObject { ["workerCode"] = code, ["name"] = name, ["line"] = line, ["kind"] = kind };
        if (minutes is not null) o["minutes"] = minutes;
        return o;
    }

    private async Task<GeneratedContent> OvertimeAsync(Guid factoryId, DateOnly fromDate, DateOnly toDate, string? lineId, CancellationToken ct)
    {
        var rows = await (from o in _db.OvertimeRequests.AsNoTracking()
                          join w in _db.Workers.AsNoTracking() on o.WorkerId equals w.Id
                          where o.FactoryId == factoryId && o.WorkDate >= fromDate && o.WorkDate <= toDate && o.Status != "Cancelled" && (lineId == null || o.LineId == lineId)
                          orderby o.WorkDate, w.WorkerCode
                          select new { o, w.WorkerCode, w.FullName }).ToListAsync(ct);
        var gaps = new List<DocumentIssue>();
        var arr = new JsonArray();
        foreach (var x in rows)
        {
            var row = new JsonObject { ["workerCode"] = x.WorkerCode, ["name"] = x.FullName, ["date"] = x.o.WorkDate.ToString("yyyy-MM-dd", Inv), ["requested"] = x.o.RequestedMinutes, ["status"] = x.o.Status };
            if (x.o.ApprovedMinutes is { } a) row["approved"] = a;
            if (x.o.ActualMinutes is { } act) row["actual"] = act;
            if (x.o.PayableMinutes is { } p) row["payable"] = p;
            arr.Add(row);
        }
        var unconfirmed = rows.Count(x => x.o.Status is "Approved" or "InProgress" or "Completed" && x.o.PayableMinutes is null);
        if (unconfirmed > 0) gaps.Add(new DocumentIssue("payableMinutes", "payable_not_confirmed", $"{unconfirmed} approved overtime record(s) have no payable minutes confirmed by HR yet."));
        var d = new JsonObject
        {
            ["requestedMinutes"] = rows.Sum(x => x.o.RequestedMinutes), ["approvedMinutes"] = rows.Sum(x => x.o.ApprovedMinutes ?? 0),
            ["actualMinutes"] = rows.Sum(x => x.o.ActualMinutes ?? 0), ["payableMinutes"] = rows.Sum(x => x.o.PayableMinutes ?? 0), ["unconfirmed"] = unconfirmed, ["rows"] = arr,
        };
        if (rows.Count == 0) gaps.Add(new DocumentIssue("rows", "no_overtime", "No overtime is recorded in this period."));
        return new GeneratedContent(d, gaps, rows.Count > 0);
    }

    private async Task<GeneratedContent> PayrollAsync(Guid factoryId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var clock = await FactoryClock.LoadAsync(_db, factoryId, ct);
        var today = clock.Today(DateTimeOffset.UtcNow);
        var sessions = await _db.AttendanceSessions.AsNoTracking()
            .Where(s => s.FactoryId == factoryId && s.WorkDate >= from && s.WorkDate <= to)
            .Select(s => new { s.WorkerId, s.WorkDate, s.CheckInAt, s.NetWorkingMinutes, s.IsLate, s.Status }).ToListAsync(ct);
        var ot = await _db.OvertimeRequests.AsNoTracking()
            .Where(o => o.FactoryId == factoryId && o.WorkDate >= from && o.WorkDate <= to && o.Status != "Cancelled" && o.Status != "Rejected")
            .Select(o => new { o.WorkerId, o.Status, o.PayableMinutes }).ToListAsync(ct);
        // Scheduled working days of each worker (active shift assignment on a working weekday), minus holidays and approved leave.
        var assignments = await (from a in _db.ShiftAssignments.AsNoTracking()
                                 join sh in _db.Shifts.AsNoTracking() on a.ShiftId equals sh.Id
                                 where a.FactoryId == factoryId && a.Status != "Superseded" && a.EffectiveFrom <= to && (a.EffectiveTo == null || a.EffectiveTo >= @from)
                                 select new { a.WorkerId, a.EffectiveFrom, a.EffectiveTo, sh.WorkingDays }).ToListAsync(ct);
        var rostered = new List<(Guid WorkerId, DateOnly Date)>();
        for (var day = from; day <= to; day = day.AddDays(1))
            foreach (var w in assignments.Where(a => a.EffectiveFrom <= day && (a.EffectiveTo == null || a.EffectiveTo >= day) && AttendanceRules.WorksOn(a.WorkingDays, day.DayOfWeek))
                         .Select(a => a.WorkerId).Distinct())
                rostered.Add((w, day));
        var calendar = await AttendanceCalendarLoader.LoadAsync(_db, new[] { factoryId }, from, to, ct);
        var (expected, excused) = calendar.Apply(rostered, x => x.WorkerId, x => x.Date, _ => factoryId);
        var presentDays = sessions.Where(s => s.CheckInAt != null).Select(s => (s.WorkerId, s.WorkDate)).ToHashSet();

        var ids = sessions.Select(s => s.WorkerId).Concat(ot.Select(o => o.WorkerId)).Concat(rostered.Select(r => r.WorkerId)).Distinct().ToList();
        var workers = await _db.Workers.AsNoTracking().Where(w => ids.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var gaps = new List<DocumentIssue>();
        var arr = new JsonArray();
        int present = 0, payable = 0, unconfirmed = 0, leaveTotal = 0, absentTotal = 0; double hours = 0;
        foreach (var w in workers.Values.OrderBy(w => w.WorkerCode))
        {
            var ws = sessions.Where(s => s.WorkerId == w.Id).ToList();
            var days = ws.Where(s => s.CheckInAt != null).Select(s => s.WorkDate).Distinct().Count();
            var leaveDays = excused.Count(e => e.WorkerId == w.Id && e.Kind == ExcusalKinds.Leave && !presentDays.Contains((w.Id, e.Date)));
            // Absence only for days that are over.
            var absentDays = expected.Count(e => e.WorkerId == w.Id && e.Date < today && !presentDays.Contains((w.Id, e.Date)));
            var h = Math.Round(ws.Sum(s => s.NetWorkingMinutes) / 60.0, 2);
            var wo = ot.Where(o => o.WorkerId == w.Id).ToList();
            var pay = wo.Sum(o => o.PayableMinutes ?? 0);
            var unc = wo.Count(o => o.PayableMinutes is null && o.Status is "Approved" or "InProgress" or "Completed");
            present += days; hours += h; payable += pay; unconfirmed += unc; leaveTotal += leaveDays; absentTotal += absentDays;
            arr.Add(new JsonObject
            {
                ["workerCode"] = w.WorkerCode, ["name"] = w.FullName, ["presentDays"] = days, ["leaveDays"] = leaveDays, ["absentDays"] = absentDays, ["netHours"] = h,
                ["lateCount"] = ws.Count(s => s.IsLate), ["payableOtMinutes"] = pay, ["unconfirmedOt"] = unc,
            });
        }
        var holidays = calendar.Holidays.Count(hd => hd.FactoryId == factoryId);
        var open = sessions.Count(s => s.Status is "MissingCheckOut" or "MissingCheckIn" or "Open");
        if (open > 0) gaps.Add(new DocumentIssue("rows", "sessions_not_final", $"{open} attendance session(s) in the period are open or missing a punch: their hours are not final."));
        if (unconfirmed > 0) gaps.Add(new DocumentIssue("unconfirmedOt", "payable_not_confirmed", $"{unconfirmed} overtime record(s) still need HR to confirm payable minutes."));
        if (to >= today) gaps.Add(new DocumentIssue("absentDays", "period_not_over", "The pay period isn't over: absence is counted only for the days already past."));
        if (assignments.Count == 0) gaps.Add(new DocumentIssue("absentDays", "no_shift_assignments", "No shift assignments in the period: leave and absent days can't be determined."));
        var d = new JsonObject
        {
            ["workers"] = arr.Count, ["presentDays"] = present, ["leaveDays"] = leaveTotal, ["absentDays"] = absentTotal, ["holidays"] = holidays, ["netHours"] = Math.Round(hours, 2),
            ["payableOtMinutes"] = payable, ["unconfirmedOt"] = unconfirmed, ["rows"] = arr,
        };
        if (arr.Count == 0) gaps.Add(new DocumentIssue("rows", "no_attendance", "No attendance is recorded in this period."));
        return new GeneratedContent(d, gaps, sessions.Count > 0 || ot.Count > 0 || (rostered.Count > 0 && to < today));
    }

    // ── Store ───────────────────────────────────────────────────────────────

    private async Task<GeneratedContent> RegisterAsync(Guid factoryId, DateOnly from, DateOnly to, string category, CancellationToken ct)
    {
        var items = await _db.InventoryItems.AsNoTracking().Where(i => i.FactoryId == factoryId && (category == "ALL" || i.Category == category)).ToDictionaryAsync(i => i.Id, ct);
        var itemIds = items.Keys.ToList();
        var opening = await _db.StockMovements.AsNoTracking().Where(m => m.FactoryId == factoryId && itemIds.Contains(m.ItemId) && m.MovementDate < from)
            .GroupBy(m => m.ItemId).Select(g => new { g.Key, Bal = g.Sum(m => m.Kind == "RECEIPT" ? m.Quantity : -m.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Bal, ct);
        var moves = await _db.StockMovements.AsNoTracking().Where(m => m.FactoryId == factoryId && itemIds.Contains(m.ItemId) && m.MovementDate >= from && m.MovementDate <= to)
            .OrderBy(m => m.MovementDate).ThenBy(m => m.Id).ToListAsync(ct);
        var balance = new Dictionary<Guid, decimal>(opening);
        var arr = new JsonArray();
        foreach (var m in moves)
        {
            var it = items[m.ItemId];
            var bal = balance.GetValueOrDefault(m.ItemId) + (m.Kind == "RECEIPT" ? m.Quantity : -m.Quantity);
            balance[m.ItemId] = bal;
            var row = new JsonObject { ["date"] = m.MovementDate.ToString("yyyy-MM-dd", Inv), ["itemCode"] = it.Code, ["itemName"] = it.Name, ["document"] = m.DocumentReference, ["counterparty"] = m.Counterparty, ["balance"] = bal };
            row[m.Kind == "RECEIPT" ? "received" : "issued"] = m.Quantity;
            arr.Add(row);
        }
        var d = new JsonObject
        {
            ["itemCategory"] = category, ["receipts"] = moves.Where(m => m.Kind == "RECEIPT").Sum(m => m.Quantity), ["issues"] = moves.Where(m => m.Kind == "ISSUE").Sum(m => m.Quantity), ["rows"] = arr,
        };
        var gaps = new List<DocumentIssue>();
        if (moves.Count == 0) gaps.Add(new DocumentIssue("rows", "no_movements", "No receipt or issue was posted in this period."));
        return new GeneratedContent(d, gaps, moves.Count > 0);
    }

    private async Task<GeneratedContent> BalanceAsync(Guid factoryId, DateOnly asOf, CancellationToken ct)
    {
        var items = await _db.InventoryItems.AsNoTracking().Where(i => i.FactoryId == factoryId && i.IsActive).OrderBy(i => i.CodeKey).ToListAsync(ct);
        var sums = await _db.StockMovements.AsNoTracking().Where(m => m.FactoryId == factoryId && m.MovementDate <= asOf)
            .GroupBy(m => new { m.ItemId, m.Kind }).Select(g => new { g.Key.ItemId, g.Key.Kind, Qty = g.Sum(m => m.Quantity) }).ToListAsync(ct);
        // Rolls with something left on them at the end of the day, per item.
        var rollLeft = await (from m in _db.InventoryRollMovements.AsNoTracking()
                              join r in _db.InventoryRolls.AsNoTracking() on m.RollId equals r.Id
                              where m.FactoryId == factoryId && m.MovementDate <= asOf
                              group m by new { m.RollId, r.ItemId } into g
                              select new { g.Key.ItemId, Left = g.Sum(m => m.Kind == "RECEIPT" ? m.Quantity : -m.Quantity) }).ToListAsync(ct);
        var rollsInStock = rollLeft.Where(x => x.Left > 0).GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g.Count());
        var arr = new JsonArray();
        var below = 0;
        foreach (var i in items)
        {
            var rec = sums.Where(s => s.ItemId == i.Id && s.Kind == "RECEIPT").Sum(s => s.Qty);
            var iss = sums.Where(s => s.ItemId == i.Id && s.Kind == "ISSUE").Sum(s => s.Qty);
            var bal = rec - iss;
            var flag = i.ReorderLevel is { } r && bal <= r ? "BELOW REORDER" : bal == 0 ? "OUT OF STOCK" : "";
            if (flag.Length > 0 && i.ReorderLevel is { } rr && bal <= rr) below++;
            var row = new JsonObject { ["itemCode"] = i.Code, ["itemName"] = i.Name, ["category"] = i.Category, ["unit"] = i.Unit, ["received"] = rec, ["issued"] = iss, ["balance"] = bal, ["flag"] = flag };
            if (rollsInStock.TryGetValue(i.Id, out var rc)) row["rolls"] = rc;
            if (i.ReorderLevel is { } lvl) row["reorderLevel"] = lvl;
            arr.Add(row);
        }
        var d = new JsonObject { ["items"] = items.Count, ["belowReorder"] = below, ["rows"] = arr };
        var gaps = new List<DocumentIssue>();
        if (items.Count == 0) gaps.Add(new DocumentIssue("rows", "no_items", "The store has no items yet: they are created when the first GRN is approved."));
        return new GeneratedContent(d, gaps, items.Count > 0);
    }

    // ── Quality ─────────────────────────────────────────────────────────────

    private sealed record QcRow(QcInspection I, string? LineName);

    private async Task<(List<QcRow> Rows, int WithoutLine)> InspectionsAsync(Guid factoryId, DateOnly fromDate, DateOnly toDate, string? lineId, QcInspectionStage? stage, FactoryClock clock, CancellationToken ct)
    {
        var start = clock.DayWindow(fromDate).Start.UtcDateTime;
        var end = clock.DayWindow(toDate).End.UtcDateTime;
        var rows = await (from i in _db.QcInspections.AsNoTracking().Include(i => i.Findings)
                          join l in _db.ProductionLines.AsNoTracking() on i.LineId equals l.Id
                          where l.FactoryId == factoryId && i.CreatedAt >= start && i.CreatedAt < end && (stage == null || i.Stage == stage) && (lineId == null || i.LineId == lineId)
                          orderby i.CreatedAt
                          select new QcRow(i, l.Name)).ToListAsync(ct);
        var withoutLine = lineId is null
            ? await _db.QcInspections.AsNoTracking().CountAsync(i => i.LineId == null && i.CreatedAt >= start && i.CreatedAt < end && (stage == null || i.Stage == stage), ct)
            : 0;
        return (rows, withoutLine);
    }

    private static IEnumerable<QcInspectionFinding> Accepted(QcInspection i) =>
        i.Findings.Where(f => f.Decision is QcFindingDecision.Accepted or QcFindingDecision.Corrected);

    private static QcDefectSeverity Sev(QcInspectionFinding f) => f.CorrectedSeverity ?? f.Severity;

    private async Task<GeneratedContent> QcStageAsync(Guid factoryId, DateOnly date, string? lineId, string stage, FactoryClock clock, CancellationToken ct)
    {
        var st = Enum.Parse<QcInspectionStage>(stage);
        var (rows, withoutLine) = await InspectionsAsync(factoryId, date, date, lineId, st, clock, ct);
        var gaps = new List<DocumentIssue>();
        var arr = new JsonArray();
        foreach (var r in rows)
        {
            var acc = Accepted(r.I).ToList();
            arr.Add(new JsonObject
            {
                ["reference"] = r.I.Reference, ["line"] = r.LineName, ["order"] = r.I.ProductionOrder, ["product"] = r.I.ProductType,
                ["outcome"] = r.I.Outcome?.ToString().ToUpperInvariant() ?? "NOT REVIEWED",
                ["critical"] = acc.Count(f => Sev(f) == QcDefectSeverity.Critical), ["major"] = acc.Count(f => Sev(f) == QcDefectSeverity.Major), ["minor"] = acc.Count(f => Sev(f) == QcDefectSeverity.Minor),
                ["reviewedBy"] = r.I.ReviewedByEmail,
            });
        }
        var pending = rows.Count(r => r.I.Outcome is null);
        if (pending > 0) gaps.Add(new DocumentIssue("pendingReview", "inspections_not_reviewed", $"{pending} inspection(s) are not reviewed yet: their defects are not counted."));
        if (withoutLine > 0) gaps.Add(new DocumentIssue("rows", "inspections_without_line", $"{withoutLine} inspection(s) of this day have no production line and can't be attributed to a factory; they are not included."));
        var d = new JsonObject
        {
            ["inspections"] = rows.Count, ["pass"] = rows.Count(r => r.I.Outcome == QcInspectionOutcome.Pass), ["rework"] = rows.Count(r => r.I.Outcome == QcInspectionOutcome.Rework),
            ["reject"] = rows.Count(r => r.I.Outcome == QcInspectionOutcome.Reject), ["pendingReview"] = pending, ["defects"] = rows.Sum(r => Accepted(r.I).Count()), ["rows"] = arr,
        };
        if (rows.Count == 0) gaps.Add(new DocumentIssue("rows", "no_inspections", "No inspection of this stage is recorded for this day."));
        return new GeneratedContent(d, gaps, rows.Count > 0);
    }

    private async Task<GeneratedContent> DefectsAsync(Guid factoryId, DateOnly from, DateOnly to, string? lineId, FactoryClock clock, CancellationToken ct)
    {
        var (rows, withoutLine) = await InspectionsAsync(factoryId, from, to, lineId, null, clock, ct);
        var reviewed = rows.Where(r => r.I.Outcome is not null).ToList();
        var findings = reviewed.SelectMany(r => Accepted(r.I)).ToList();
        var names = await _db.QcDefectCategories.AsNoTracking().ToDictionaryAsync(c => c.Code, c => c.Name, ct);
        var arr = new JsonArray();
        foreach (var g in findings.GroupBy(f => f.CorrectedCategoryCode ?? f.CategoryCode ?? "uncategorised").OrderByDescending(g => g.Count()))
            arr.Add(new JsonObject
            {
                ["category"] = names.GetValueOrDefault(g.Key, g.Key), ["critical"] = g.Count(f => Sev(f) == QcDefectSeverity.Critical),
                ["major"] = g.Count(f => Sev(f) == QcDefectSeverity.Major), ["minor"] = g.Count(f => Sev(f) == QcDefectSeverity.Minor), ["total"] = g.Count(),
            });
        var gaps = new List<DocumentIssue>();
        var d = new JsonObject { ["inspections"] = reviewed.Count, ["defects"] = findings.Count, ["rows"] = arr };
        if (reviewed.Count > 0) d["dhu"] = Math.Round(findings.Count * 100.0 / reviewed.Count, 2);
        var pending = rows.Count - reviewed.Count;
        if (pending > 0) gaps.Add(new DocumentIssue("inspections", "inspections_not_reviewed", $"{pending} inspection(s) are not reviewed yet and are not counted."));
        if (withoutLine > 0) gaps.Add(new DocumentIssue("inspections", "inspections_without_line", $"{withoutLine} inspection(s) have no production line and are not included."));
        if (reviewed.Count == 0) gaps.Add(new DocumentIssue("rows", "no_inspections", "No reviewed inspection in this period."));
        return new GeneratedContent(d, gaps, reviewed.Count > 0);
    }

    private async Task<GeneratedContent> ReworkAsync(Guid factoryId, DateOnly from, DateOnly to, string? lineId, FactoryClock clock, CancellationToken ct)
    {
        var outputs = await OutputsAsync(factoryId, from, to, lineId, ct);
        var (inspections, _) = await InspectionsAsync(factoryId, from, to, lineId, null, clock, ct);
        var arr = new JsonArray();
        foreach (var g in outputs.GroupBy(r => (r.LineName, r.Order)).OrderBy(g => g.Key.LineName).ThenBy(g => g.Key.Order))
        {
            var good = g.Where(r => r.Stage == ProductionStage.SewingOutput).Sum(r => r.Qty);
            var rw = g.Sum(r => r.Rework); var rj = g.Sum(r => r.Reject);
            var row = new JsonObject { ["line"] = g.Key.LineName, ["order"] = g.Key.Order, ["good"] = good, ["rework"] = rw, ["reject"] = rj };
            if (good + rw + rj > 0) row["reworkRate"] = Math.Round(rw * 100.0 / (good + rw + rj), 2);
            arr.Add(row);
        }
        var tg = outputs.Where(r => r.Stage == ProductionStage.SewingOutput).Sum(r => r.Qty);
        var trw = outputs.Sum(r => r.Rework); var trj = outputs.Sum(r => r.Reject);
        var d = new JsonObject { ["good"] = tg, ["rework"] = trw, ["reject"] = trj, ["inspectionsRework"] = inspections.Count(i => i.I.Outcome == QcInspectionOutcome.Rework), ["rows"] = arr };
        if (tg + trw + trj > 0) d["reworkRate"] = Math.Round(trw * 100.0 / (tg + trw + trj), 2);
        var gaps = new List<DocumentIssue>();
        if (outputs.Count == 0) gaps.Add(new DocumentIssue("rows", "no_output_recorded", "No production output is recorded in this period."));
        return new GeneratedContent(d, gaps, outputs.Count > 0 || inspections.Count > 0);
    }

    // ── Devices ─────────────────────────────────────────────────────────────

    private async Task<GeneratedContent> DeviceLogAsync(Guid factoryId, DateOnly from, DateOnly to, FactoryClock clock, CancellationToken ct)
    {
        var start = clock.DayWindow(from).Start;
        var end = clock.DayWindow(to).End;
        var events = await _db.DeviceEvents.AsNoTracking().Where(e => e.FactoryId == factoryId && e.OccurredAt >= start && e.OccurredAt < end)
            .Select(e => new { e.DeviceId, e.EventType, e.Severity, e.OccurredAt, e.Message, e.Code, e.ToState, e.IsSimulated }).ToListAsync(ct);
        var devices = await _db.Devices.AsNoTracking().Where(d => d.FactoryId == factoryId).ToDictionaryAsync(d => d.Id, ct);
        var table = new JsonArray();
        foreach (var g in events.GroupBy(e => e.DeviceId).OrderBy(g => devices.GetValueOrDefault(g.Key)?.Code))
        {
            var dev = devices.GetValueOrDefault(g.Key);
            table.Add(new JsonObject
            {
                ["device"] = dev?.Code ?? g.Key.ToString()[..8], ["category"] = dev?.Category, ["events"] = g.Count(), ["errors"] = g.Count(e => e.Severity == "Error"),
                ["warnings"] = g.Count(e => e.Severity == "Warning"), ["offline"] = g.Count(e => e.ToState == "Offline"), ["simulated"] = dev?.IsSimulated == true ? "yes" : "no",
            });
        }
        var issues = new JsonArray();
        foreach (var e in events.Where(e => e.Severity is "Error" or "Warning").OrderByDescending(e => e.OccurredAt).Take(200))
            issues.Add(new JsonObject
            {
                ["at"] = Local(e.OccurredAt, clock), ["device"] = devices.GetValueOrDefault(e.DeviceId)?.Code, ["type"] = e.EventType, ["severity"] = e.Severity,
                ["message"] = e.Message ?? e.Code,
            });
        var d = new JsonObject
        {
            ["devices"] = table.Count, ["events"] = events.Count, ["errors"] = events.Count(e => e.Severity == "Error"), ["offline"] = events.Count(e => e.ToState == "Offline"),
            ["devicesTable"] = table, ["issues"] = issues,
        };
        var gaps = new List<DocumentIssue>();
        if (events.Count == 0) gaps.Add(new DocumentIssue("devicesTable", "no_events", "No device event is recorded in this period."));
        if (events.Any(e => e.IsSimulated)) gaps.Add(new DocumentIssue("devicesTable", "simulated_events", "Some events come from SIMULATED devices."));
        return new GeneratedContent(d, gaps, events.Count > 0);
    }
}
