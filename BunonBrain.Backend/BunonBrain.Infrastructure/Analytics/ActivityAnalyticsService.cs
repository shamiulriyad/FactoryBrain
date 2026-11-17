using System.Runtime.CompilerServices;
using System.Text;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Analytics;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Analytics;
using BunonBrain.Application.Monitoring;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Analytics;

/// <summary>
/// Aggregate AI activity observations from the camera AI's monitoring
/// events: predicted-label distribution, trends, lines, confidence bands,
/// camera coverage and how many observed tracks have a VERIFIED identity.
/// Grouping by the predicted label needs the JSON payload, so these run as
/// parameterised SQL with the same reach rules as the monitoring queries
/// (factory, and line for supervisors). Everything here is an estimate;
/// nothing is per worker.
/// </summary>
public sealed class ActivityAnalyticsService : IActivityAnalytics
{
    public const string Disclaimer =
        "AI-derived observations, not verified production facts. A pose or activity prediction is not proof of completed work; a camera track id is a temporary " +
        "number, not a worker id; only VERIFIED identity associations link observations to workers, and they are counted only in aggregate. These figures are " +
        "never used for pay, deductions, discipline or ranking individual workers.";

    private readonly BunonBrainDbContext _db;

    public ActivityAnalyticsService(BunonBrainDbContext db) { _db = db; }

    public async Task<AccessResult<ActivityInsightsDto>> ActivityAsync(AccessContext caller, AnalyticsFilter filter, CancellationToken ct)
    {
        var c = await AnalyticsContext.CreateAsync(_db, caller, filter, ct);
        if (c.Outcome != AccessOutcome.Ok) return new AccessResult<ActivityInsightsDto>(c.Outcome, null, c.Code, c.Message);
        if (!c.Value!.Can(AnalyticsSections.Activity))
            return AccessResult<ActivityInsightsDto>.Forbidden("monitoring_access_required", "AI activity insights need monitoring.view.");
        return AccessResult<ActivityInsightsDto>.Ok(await BuildAsync(c.Value, ct));
    }

    // ─── SQL scope ─────────────────────────────────────────────────────────

    /// <summary>WHERE conditions (with positional arguments) for the request's vision events.</summary>
    internal static (string Where, List<object> Args) Scope(AnalyticsContext ctx, string[] types)
    {
        var args = new List<object>();
        string P(object v) { args.Add(v); return $"{{{args.Count - 1}}}"; }
        var w = new StringBuilder();
        w.Append($"e.\"EventType\" = ANY({P(types)}) AND e.\"OccurredAt\" >= {P(ctx.Period.FromUtc)} AND e.\"OccurredAt\" < {P(ctx.Period.ToUtc)}");
        w.Append($" AND e.\"SourceType\" = {P(ctx.Simulated ? MonitoringSourceTypes.Simulated : MonitoringSourceTypes.Real)}");
        var caller = ctx.Caller;
        switch (caller.Scope)
        {
            case RoleScope.Platform: break;
            case RoleScope.Factory: w.Append($" AND e.\"FactoryId\" = ANY({P(caller.FactoryIds.ToArray())})"); break;
            case RoleScope.Line:
                w.Append($" AND e.\"FactoryId\" = ANY({P(caller.FactoryIds.ToArray())}) AND e.\"LineId\" = ANY({P(caller.LineIds.ToArray())})");
                break;
            default: w.Append(" AND FALSE"); break;
        }
        var f = ctx.Filter;
        if (f.FactoryId is { } fid) w.Append($" AND e.\"FactoryId\" = {P(fid)}");
        if (!string.IsNullOrWhiteSpace(f.LineId)) w.Append($" AND e.\"LineId\" = {P(f.LineId.Trim())}");
        if (f.DepartmentId is { } d) w.Append($" AND e.\"DepartmentId\" = {P(d)}");
        if (f.FloorId is { } fl) w.Append($" AND e.\"FloorId\" = {P(fl)}");
        if (f.WorkstationId is { } ws) w.Append($" AND e.\"WorkstationId\" = {P(ws)}");
        if (ctx.EventSource == "SIMULATOR") w.Append($" AND e.\"Producer\" = {P(MonitoringProducers.Simulator)}");
        else if (ctx.EventSource == "DEVICE") w.Append($" AND e.\"Producer\" <> {P(MonitoringProducers.Simulator)}");
        else if (ctx.EventSource == "MANUAL") w.Append(" AND FALSE");
        return (w.ToString(), args);
    }

    private IQueryable<T> Query<T>(string sql, List<object> args) => _db.Database.SqlQuery<T>(FormattableStringFactory.Create(sql, args.ToArray()));

    public sealed class LabelRow { public DateOnly Day { get; set; } public string LineId { get; set; } = ""; public string Activity { get; set; } = ""; public bool Uncertain { get; set; } public long N { get; set; } }
    public sealed class BandRow { public string Band { get; set; } = ""; public long N { get; set; } }
    public sealed class CameraRow { public Guid CameraId { get; set; } public long Hours { get; set; } public long Tracks { get; set; } public long Predictions { get; set; } public double Seconds { get; set; } public DateTimeOffset? LastAt { get; set; } }

    internal async Task<ActivityInsightsDto> BuildAsync(AnalyticsContext ctx, CancellationToken ct)
    {
        var family = ActivityFamily.For(ctx.Filter.Family);
        var uncertainType = family.Uncertain;
        var tz = ctx.Zone.Id;

        // 1. Predictions per local day, line and predicted label.
        var (w1, a1) = Scope(ctx, family.Types);
        var tzArg = a1.Count; a1.Add(tz);
        var unc = a1.Count; a1.Add(uncertainType);
        var labels = await Query<LabelRow>(
            $"SELECT (e.\"OccurredAt\" AT TIME ZONE {{{tzArg}}})::date AS \"Day\", COALESCE(e.\"LineId\", '') AS \"LineId\", " +
            $"UPPER(COALESCE(e.\"Payload\"::jsonb ->> 'activity', 'UNKNOWN')) AS \"Activity\", (e.\"EventType\" = {{{unc}}}) AS \"Uncertain\", count(*) AS \"N\" " +
            $"FROM monitoring_events e WHERE {w1} GROUP BY 1, 2, 3, 4", a1).ToListAsync(ct);

        // 2. Confidence bands (predictions with a confidence).
        var (w2, a2) = Scope(ctx, family.Types);
        var bands = await Query<BandRow>(
            "SELECT CASE WHEN e.\"Confidence\" < 0.5 THEN '< 0.5' WHEN e.\"Confidence\" < 0.7 THEN '0.5 – 0.7' WHEN e.\"Confidence\" < 0.9 THEN '0.7 – 0.9' ELSE '≥ 0.9' END AS \"Band\", " +
            $"count(*) AS \"N\" FROM monitoring_events e WHERE {w2} AND e.\"Confidence\" IS NOT NULL GROUP BY 1", a2).ToListAsync(ct);

        // 3. Camera coverage: hours with any vision event, tracks, predictions, observation time (per track last − first, capped at 12 h).
        var (w3, a3) = Scope(ctx, MonitoringEventTypes.Vision);
        var fam = a3.Count; a3.Add(family.Types);
        var cameras = await Query<CameraRow>(
            "SELECT t.\"CameraId\" AS \"CameraId\", max(h.\"Hours\") AS \"Hours\", count(*) AS \"Tracks\", sum(t.\"Predictions\")::bigint AS \"Predictions\", " +
            "sum(LEAST(EXTRACT(EPOCH FROM (t.\"Last\" - t.\"First\")), 43200))::float8 AS \"Seconds\", max(t.\"Last\") AS \"LastAt\" FROM (" +
            "  SELECT e.\"CameraId\", e.\"TrackId\", min(e.\"OccurredAt\") AS \"First\", max(e.\"OccurredAt\") AS \"Last\", " +
            $"  count(*) FILTER (WHERE e.\"EventType\" = ANY({{{fam}}})) AS \"Predictions\" FROM monitoring_events e " +
            $"  WHERE {w3} AND e.\"CameraId\" IS NOT NULL AND e.\"TrackId\" IS NOT NULL GROUP BY 1, 2) t " +
            "JOIN (SELECT e.\"CameraId\", count(DISTINCT date_trunc('hour', e.\"OccurredAt\")) AS \"Hours\" FROM monitoring_events e " +
            $"  WHERE {w3} AND e.\"CameraId\" IS NOT NULL GROUP BY 1) h ON h.\"CameraId\" = t.\"CameraId\" GROUP BY 1", a3).ToListAsync(ct);

        // 4. Identity: tracks with a VERIFIED association during the period, ambiguous / review ones.
        var assoc = ctx.Associations();
        var verifiedTracks = await assoc.Where(a => a.Status == "VERIFIED").Select(a => new { a.CameraId, a.TrackId }).Distinct().CountAsync(ct);
        var reviewTracks = await assoc.Where(a => a.Status == "AMBIGUOUS" || a.Status == "MANUAL_REVIEW").Select(a => new { a.CameraId, a.TrackId }).Distinct().CountAsync(ct);
        var verifiedWorkers = await assoc.Where(a => a.Status == "VERIFIED" && a.WorkerId != null).Select(a => a.WorkerId).Distinct().CountAsync(ct);
        var tracksObserved = cameras.Sum(x => x.Tracks);

        // Shape.
        var total = labels.Sum(x => x.N);
        var uncertain = labels.Where(x => x.Uncertain || x.Activity == ActivityLabels.Unknown).Sum(x => x.N);
        List<ActivityShare> Shares(IEnumerable<LabelRow> rows)
        {
            var list = rows.GroupBy(x => x.Activity).Select(g => (Activity: g.Key, N: g.Sum(x => x.N))).ToList();
            var sum = list.Sum(x => x.N);
            var order = family.Labels.ToList();
            return list.OrderBy(x => order.IndexOf(x.Activity) is var i && i < 0 ? int.MaxValue : i).ThenBy(x => x.Activity)
                .Select(x => new ActivityShare(x.Activity, x.N, AnalyticsMath.Pct(x.N, sum))).ToList();
        }
        var daily = ctx.Period.Dates().Select(d =>
        {
            var rows = labels.Where(x => x.Day == d).ToList();
            return new ActivityDayRow(d, rows.Sum(x => x.N), rows.Where(x => x.Uncertain || x.Activity == ActivityLabels.Unknown).Sum(x => x.N), Shares(rows));
        }).ToList();
        var lineNames = await ctx.LineNamesAsync(labels.Select(x => x.LineId == "" ? null : x.LineId), ct);
        var byLine = labels.GroupBy(x => x.LineId).Select(g => new ActivityLineRow(g.Key, g.Key == "" ? "No line" : lineNames.GetValueOrDefault(g.Key, g.Key),
            g.Sum(x => x.N), g.Where(x => x.Uncertain || x.Activity == ActivityLabels.Unknown).Sum(x => x.N), Shares(g))).OrderBy(x => x.LineName).ToList();
        var withConfidence = bands.Sum(b => b.N);
        var confidence = ConfidenceBands.All.Select(b => new ConfidenceBand(b.Label, bands.FirstOrDefault(x => x.Band == b.Label)?.N ?? 0,
            AnalyticsMath.Pct(bands.FirstOrDefault(x => x.Band == b.Label)?.N ?? 0, withConfidence))).ToList();
        var camIds = cameras.Select(x => x.CameraId).ToList();
        var camCodes = await _db.Cameras.AsNoTracking().Where(x => camIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Code, ct);
        var camRows = cameras.OrderBy(x => camCodes.GetValueOrDefault(x.CameraId)).Select(x => new CameraCoverageRow(x.CameraId, camCodes.GetValueOrDefault(x.CameraId, "?"),
            x.Hours, x.Tracks, x.Predictions, AnalyticsMath.Hours(x.Seconds), x.LastAt)).ToList();

        var kpis = new List<Kpi>
        {
            Kpi.Count("ai.predictions", total, total == 0 ? "The camera AI sent no activity predictions in this scope and period." : null, total == 0 ? Availability.NoData : Availability.Ok),
            Kpi.Ratio("ai.uncertain_share", uncertain, total),
            Kpi.Count("ai.tracks", tracksObserved, tracksObserved == 0 ? null : "Tracks are temporary camera numbers, not people or workers"),
            Kpi.Count("ai.observation_hours", AnalyticsMath.Hours(cameras.Sum(x => x.Seconds))),
            Kpi.Ratio("ai.verified_tracks", verifiedTracks, tracksObserved, tracksObserved == 0 ? null : $"{verifiedTracks} of {tracksObserved} observed tracks"),
            Kpi.Count("ai.verified_workers", verifiedWorkers),
        };
        var warnings = ctx.CommonWarnings().ToList();
        if (total == 0) warnings.Add("No activity predictions: the distribution is empty, not zero activity.");
        if (ctx.Filter.ShiftId is not null || ctx.Filter.MachineId is not null) warnings.Add("Camera observations are not split by shift or machine: those filters don't apply here.");
        var activeCameras = await _db.Cameras.AsNoTracking().CountAsync(x => x.Status == "Active" && (ctx.Filter.FactoryId == null || x.FactoryId == ctx.Filter.FactoryId)
            && (ctx.Caller.Scope == RoleScope.Platform || ctx.Caller.FactoryIds.Contains(x.FactoryId)), ct);
        if (activeCameras > camRows.Count) warnings.Add($"{activeCameras - camRows.Count} active camera(s) sent no observation in this period.");

        return new ActivityInsightsDto(ctx.Dto(), family.Kind, kpis, Shares(labels), daily, byLine, confidence, camRows,
            new IdentityCoverage(tracksObserved, verifiedTracks, Math.Max(0, tracksObserved - verifiedTracks), reviewTracks, verifiedWorkers,
                "Only VERIFIED associations count as confirmed; AMBIGUOUS, UNVERIFIED and review cases never do."),
            Disclaimer, warnings);
    }

    /// <summary>Active cameras and how many sent a vision event in the period (for coverage insights).</summary>
    internal async Task<(int Active, int Reporting)> CameraCoverageAsync(AnalyticsContext ctx, CancellationToken ct)
    {
        var reporting = await ctx.Events().Where(e => e.CameraId != null && MonitoringEventTypes.Vision.Contains(e.EventType)).Select(e => e.CameraId).Distinct().CountAsync(ct);
        var cams = _db.Cameras.AsNoTracking().Where(x => x.Status == "Active");
        if (ctx.Filter.FactoryId is { } f) cams = cams.Where(x => x.FactoryId == f);
        else if (ctx.Caller.Scope != RoleScope.Platform) { var fs = ctx.Caller.FactoryIds.ToList(); cams = cams.Where(x => fs.Contains(x.FactoryId)); }
        if (!string.IsNullOrWhiteSpace(ctx.Filter.LineId)) { var l = ctx.Filter.LineId.Trim(); cams = cams.Where(x => x.LineId == l); }
        else if (ctx.Caller.Scope == RoleScope.Line) { var ls = ctx.Caller.LineIds.ToList(); cams = cams.Where(x => x.LineId != null && ls.Contains(x.LineId)); }
        return (await cams.CountAsync(ct), reporting);
    }
}
