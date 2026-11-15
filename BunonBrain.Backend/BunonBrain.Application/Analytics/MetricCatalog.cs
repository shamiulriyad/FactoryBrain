namespace BunonBrain.Application.Analytics;

// The metric definition layer. Every number the analytics pages, insights
// and reports show refers to one of these definitions, which say where it
// comes from, how it is calculated, over which window, what is in and out,
// what it is divided by, what happens when data is missing, and whether it
// is measured, derived or an AI estimate. The definitions are served at
// GET /api/analytics/metrics and printed in every report.
//
// The independent quantities are never combined into one score:
// attendance duration, workstation assignment, machine running time,
// AI-observed activity, verified production count and approved overtime.

public static class MetricKinds
{
    /// <summary>Counted directly from records (punches, counter events, state transitions).</summary>
    public const string Measured = "MEASURED";
    /// <summary>Calculated from measured records (ratios, durations, plans).</summary>
    public const string Derived = "DERIVED";
    /// <summary>From camera AI predictions: an observation, never a verified fact.</summary>
    public const string AiEstimate = "AI_ESTIMATE";
}

public sealed record MetricDefinition(
    string Id,
    string Name,
    string Kind,
    string Unit,
    IReadOnlyList<string> Sources,
    string Formula,
    string Window,
    string Includes,
    string Excludes,
    string? Denominator,
    string MissingData);

public static class MetricCatalog
{
    private const string Range = "The selected date range, as factory-local days (Asia/Dhaka unless the factory's attendance policy says otherwise); timestamps are stored in UTC.";
    private const string Now = "The current moment (live).";
    private const string Source = "Only the selected data source (REAL by default, or SIMULATED); never both.";

    private static MetricDefinition M(string id, string name, string kind, string unit, string[] sources, string formula, string window, string includes,
        string excludes, string? denominator, string missing) => new(id, name, kind, unit, sources, formula, window, includes, excludes, denominator, missing);

    public static readonly IReadOnlyList<MetricDefinition> All = new[]
    {
        // ── Workforce ──
        M("workers.registered", "Registered workers", MetricKinds.Measured, "workers", new[] { "worker_profiles" },
            "count(workers with status Active)", Now, "Active workers in scope (factory, department, floor, line).", "Inactive, suspended, resigned and terminated workers.", null,
            "0 when none are registered."),
        M("workers.scheduled", "Scheduled workers", MetricKinds.Derived, "workers", new[] { "worker_shift_assignments", "shifts" },
            "count(active workers with a shift assignment in force on the day whose shift works that weekday; optionally one shift)", Range,
            "One count per worker and day.", "Workers without a shift assignment; leave and holidays are not modelled yet.", null,
            "Unavailable when no shift assignments exist: shown as 'no schedule data', never as zero absence."),
        M("attendance.present", "Present", MetricKinds.Measured, "worker-days", new[] { "attendance_sessions" },
            "count(distinct worker × work date with a check-in)", Range, "Sessions with a check-in, from any punch source (RFID device, manual, simulator per the data filter).",
            "Camera detections never count as attendance.", "workers.scheduled (for the rate)", "0 when there are no sessions."),
        M("attendance.rate", "Attendance rate", MetricKinds.Derived, "%", new[] { "attendance_sessions", "worker_shift_assignments" },
            "scheduled workers who checked in ÷ scheduled workers × 100", Range, "Only scheduled worker-days of days whose shift has started.",
            "Unscheduled check-ins (counted in Present, not in the rate).", "Scheduled worker-days whose shift has started",
            "Unavailable (not 0 %) when nobody is scheduled."),
        M("attendance.absent", "Absent", MetricKinds.Derived, "worker-days", new[] { "attendance_sessions", "worker_shift_assignments", "shifts" },
            "scheduled worker-days with no session, once the shift start + late grace has passed", Range, "Scheduled workers only.",
            "Days whose shift hasn't started yet; leave is not modelled (counted as absent).", null, "Unavailable without schedules."),
        M("attendance.late", "Late arrivals", MetricKinds.Measured, "worker-days", new[] { "attendance_sessions" },
            "count(sessions with IsLate) — late = check-in after shift start + grace", Range, "Sessions with a shift.", "Sessions without a scheduled start.", "attendance.present",
            "0 when no sessions."),
        M("attendance.early_departure", "Early departures", MetricKinds.Measured, "worker-days", new[] { "attendance_sessions" },
            "count(sessions with IsEarlyDeparture) — before shift end − grace", Range, "Completed sessions with a shift.", "Open sessions.", "attendance.present", "0 when no sessions."),
        M("attendance.on_break", "On break now", MetricKinds.Measured, "workers", new[] { "break_sessions" },
            "count(active break sessions of open attendance sessions)", Now, "Open sessions only.", "Ended breaks.", null, "0 when nobody is checked in."),
        M("attendance.minutes", "Attendance duration", MetricKinds.Measured, "minutes", new[] { "attendance_sessions" },
            "Σ AttendanceMinutes (check-in to check-out)", Range, "Completed and open sessions (open ones up to now).", "Breaks are not subtracted here (see net working minutes).",
            null, "Sessions missing a punch count only what was punched and are flagged for review."),
        M("attendance.break_minutes", "Break duration", MetricKinds.Measured, "minutes", new[] { "attendance_sessions", "break_sessions" },
            "Σ BreakMinutes; over-break = Σ OverBreakMinutes", Range, "Recorded breaks.", "Unrecorded breaks.", "attendance.present (for the average)", "0 when no breaks."),
        M("overtime.approved", "Approved overtime", MetricKinds.Measured, "minutes", new[] { "overtime_requests" },
            "Σ ApprovedMinutes of requests Approved, InProgress or Completed", Range, "Requests by work date.", "Requested, pending, rejected, cancelled.", null, "0 when none."),
        M("overtime.actual", "Actual overtime", MetricKinds.Measured, "minutes", new[] { "overtime_requests", "attendance_sessions" },
            "Σ ActualMinutes (worked beyond the shift inside the approved window, from punches)", Range, "Approved requests with punches.", "Time beyond the shift without approval (shown separately as beyond-shift).",
            "overtime.approved (for the ratio)", "0 when none."),
        M("overtime.beyond_shift", "Time beyond shift", MetricKinds.Measured, "minutes", new[] { "attendance_sessions" },
            "Σ BeyondShiftMinutes", Range, "All sessions.", "—", null, "0 when none."),
        M("assignment.coverage", "Assignment coverage", MetricKinds.Derived, "%", new[] { "workstation_assignments", "worker_profiles" },
            "active workers with an active workstation assignment ÷ active workers × 100", Now, "Primary and secondary assignments (a worker counts once).", "Ended assignments.",
            "Active workers in scope", "Unavailable when there are no active workers."),
        M("assignment.unassigned", "Unassigned workers", MetricKinds.Derived, "workers", new[] { "workstation_assignments", "worker_profiles" },
            "active workers − assigned workers", Now, "Active workers.", "—", null, "0 when everyone is assigned."),
        M("assignment.empty_workstations", "Workstations without an assignment", MetricKinds.Derived, "workstations", new[] { "workstations", "workstation_assignments" },
            "active workstations with no active assignment", Now, "Active workstations.", "Inactive workstations.", "Active workstations", "0 when all are assigned."),
        M("staffing.required", "Planned operators", MetricKinds.Derived, "operators", new[] { "production_plans" },
            "Σ Operators of active / completed plans covering the day, per line", Range, "Plans whose dates cover the day.", "Draft and cancelled plans.",
            null, "No staffing requirement when no plan covers the line: shortages are not shown."),

        // ── Production ──
        M("lines.active", "Active production lines", MetricKinds.Measured, "lines", new[] { "production_lines", "production_counter_events" },
            "count(active lines in scope); numerator = lines with counted output in the range", Range, "Active lines attached to a factory in scope.", "Inactive lines.",
            null, "0 when no line is set up."),
        M("production.pieces", "Production output", MetricKinds.Measured, "pieces", new[] { "production_counter_events", "production_corrections" },
            "Σ Pieces of counter events (counting rule applied at ingestion) + Σ corrections", Range, "Counter events stored once per device + message id; corrections by production date. " + Source,
            "Duplicates (never stored twice), baselines, out-of-order and suspect readings (0 pieces), resets (0 pieces). Camera activity predictions are never production.",
            null, "0 with a 'no counter data' warning when no counter reported in scope."),
        M("production.pieces_per_hour", "Pieces per hour", MetricKinds.Derived, "pieces/h", new[] { "production_counter_events" },
            "production.pieces ÷ elapsed hours of the range (up to now)", Range, "As production.pieces.", "—", "Elapsed hours", "Unavailable for ranges under 1 minute."),
        M("production.planned", "Planned output", MetricKinds.Derived, "pieces", new[] { "production_plans", "production_shifts", "production_styles" },
            "Σ over active / completed plans of the planned pieces in the range (target rate from operators × efficiency ÷ SMV, or the planned quantity spread over scheduled minutes)",
            Range, "Plans of lines in scope.", "Draft and cancelled plans.", null, "Planned vs actual is shown only where a plan exists; never invented."),
        M("production.achievement", "Plan achievement", MetricKinds.Derived, "%", new[] { "production_counter_events", "production_plans" },
            "production.pieces ÷ production.planned × 100 (per line)", Range, "Lines with planned output > 0.", "Lines without a plan.", "production.planned",
            "Unavailable without a plan. Counter pieces are the machine-counted output; check the counting rule matches the plan's unit."),
        M("production.resets", "Counter resets", MetricKinds.Measured, "events", new[] { "production_counter_events" },
            "count(events with status RESET)", Range, "Device-reported and manual resets.", "—", null, "0 when none."),
        M("production.corrections", "Corrections", MetricKinds.Measured, "pieces", new[] { "production_corrections" },
            "Σ PiecesDelta (audited, with reasons)", Range, "Corrections by production date.", "—", null, "0 when none."),
        M("production.excluded", "Readings not counted", MetricKinds.Measured, "events", new[] { "production_counter_events" },
            "count(events BASELINE, OUT_OF_ORDER or SUSPECT_JUMP)", Range, "Absolute-counter readings that added no pieces.", "Duplicates (never stored).", null, "0 when none."),

        // ── Machines ──
        M("machine.running", "Running time", MetricKinds.Measured, "hours", new[] { "machine_state_transitions" },
            "Σ time in RUNNING (state lasts until the next transition)", Range, "Derived from valid sensor telemetry. " + Source,
            "Does not prove anyone operated the machine; never worker working time.", null, "0 when no state data."),
        M("machine.known_time", "Confirmed-state time", MetricKinds.Derived, "hours", new[] { "machine_state_transitions" },
            "running + idle + stopped + error", Range, "States confirmed by sensor telemetry.", "OFFLINE, UNKNOWN (no reliable data) and MAINTENANCE.", null, "0 when no state data."),
        M("machine.uptime", "Uptime", MetricKinds.Derived, "%", new[] { "machine_state_transitions" },
            "running ÷ (running + idle + stopped + error) × 100", Range, "Confirmed-state time only.",
            "OFFLINE and UNKNOWN time (a missing heartbeat is never downtime) and planned MAINTENANCE.", "machine.known_time",
            "Unavailable when there is no confirmed-state time."),
        M("machine.downtime", "Confirmed downtime", MetricKinds.Measured, "hours", new[] { "machine_state_transitions" },
            "Σ time in STOPPED + ERROR; downtime % = that ÷ confirmed-state time × 100", Range, "Sensor-confirmed stopped and error time.",
            "OFFLINE / UNKNOWN (reported as data gaps), MAINTENANCE, IDLE (reported separately).", "machine.known_time", "Unavailable without confirmed-state time."),
        M("machine.data_gap", "Data gap", MetricKinds.Derived, "hours", new[] { "machine_state_transitions", "devices" },
            "Σ time OFFLINE + UNKNOWN; coverage % = confirmed-state ÷ (period − maintenance) × 100", Range, "Monitored machines.", "Unmonitored machines (no sensor).",
            "Period minus maintenance", "Shows how much of the period has no reliable machine data."),
        M("machine.errors", "Machine errors", MetricKinds.Measured, "events", new[] { "machine_state_transitions" },
            "count(transitions into ERROR)", Range, "Sensor-reported errors.", "—", null, "0 when none."),
        M("machine.states_now", "Machines by state", MetricKinds.Measured, "machines", new[] { "machine_states", "devices" },
            "count(monitored machines per current derived state)", Now, "Machines with a bound sensor or counter.", "Machines without devices (shown as not monitored).", null,
            "Unknown until the first telemetry."),

        // ── Devices, alerts, coverage ──
        M("devices.online", "Devices online", MetricKinds.Measured, "devices", new[] { "devices", "device_heartbeats" },
            "devices whose last message is within their offline window ÷ devices × 100", Now, "Enabled devices of the source.", "Disabled devices.", "Enabled devices",
            "Unavailable when there are no devices."),
        M("alerts.open", "Open operational alerts", MetricKinds.Measured, "alerts", new[] { "monitoring_alerts" },
            "count(alerts OPEN, ACKNOWLEDGED or ESCALATED)", Now, "Alerts you may see (VISION alerts need monitoring.view). " + Source, "Resolved and dismissed alerts.", null, "0 when none."),
        M("cameras.coverage", "Camera coverage", MetricKinds.Measured, "camera-hours", new[] { "monitoring_events", "cameras" },
            "distinct (camera, hour) pairs with at least one vision event; reporting cameras ÷ active cameras", Range, "Active cameras.", "Inactive cameras.", "Active cameras × hours",
            "Low coverage means few observations, not few people."),

        // ── AI activity (estimates) ──
        M("ai.predictions", "Activity predictions", MetricKinds.AiEstimate, "predictions", new[] { "monitoring_events (ACTIVITY_*, GARMENT_ACTIVITY_*)" },
            "count(activity prediction events per predicted label)", Range, "Generic posture or garment activity (choose one family). " + Source,
            "Not proof of work; never per-worker rankings, pay or discipline.", "All predictions of the family (for the share)", "0 when the camera AI sent nothing."),
        M("ai.uncertain_share", "Uncertain predictions", MetricKinds.AiEstimate, "%", new[] { "monitoring_events" },
            "UNCERTAIN events (or label UNKNOWN) ÷ all predictions × 100", Range, "As ai.predictions.", "—", "ai.predictions", "Unavailable without predictions."),
        M("ai.confidence", "Prediction confidence", MetricKinds.AiEstimate, "share", new[] { "monitoring_events.Confidence" },
            "predictions per confidence band (<0.5, 0.5–0.7, 0.7–0.9, ≥0.9)", Range, "Predictions with a confidence.", "Uncertain events without a confidence.", "ai.predictions",
            "Confidence is the model's own score, not accuracy."),
        M("ai.tracks", "Camera tracks observed", MetricKinds.AiEstimate, "tracks", new[] { "monitoring_events" },
            "distinct (camera, track id)", Range, "Vision events.", "A track is a temporary camera number: one person can be several tracks; tracks are not workers.",
            null, "0 when the camera AI sent nothing."),
        M("ai.observation_hours", "Observation duration", MetricKinds.AiEstimate, "hours", new[] { "monitoring_events" },
            "Σ over tracks of (last − first event), each track capped at 12 h", Range, "Vision events with a track.", "—", null, "0 when nothing observed."),
        M("ai.verified_tracks", "Verified track observations", MetricKinds.Derived, "tracks", new[] { "worker_track_associations" },
            "observed tracks with a VERIFIED identity association (RFID + reader location + assignment + camera zone) during the range", Range,
            "VERIFIED associations only.", "AMBIGUOUS, UNVERIFIED, MANUAL_REVIEW and expired-before-range associations are never treated as confirmed.", "ai.tracks",
            "0 when no association exists."),
        M("ai.verified_workers", "Workers with verified camera identity", MetricKinds.Derived, "workers", new[] { "worker_track_associations" },
            "distinct workers of VERIFIED associations in the range", Range, "VERIFIED only.", "Everything else.", null, "0 when none."),
    };

    private static readonly Dictionary<string, MetricDefinition> ById = All.ToDictionary(m => m.Id, StringComparer.Ordinal);

    public static MetricDefinition Get(string id) => ById.TryGetValue(id, out var m) ? m : throw new KeyNotFoundException($"Unknown metric '{id}'.");

    public static bool Exists(string id) => ById.ContainsKey(id);
}
