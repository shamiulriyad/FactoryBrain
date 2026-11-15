# Factory analytics, operational insights and reporting

Factory-level decision support built on the records the platform already
keeps. It shows measured facts, derived figures and AI estimates — each
labelled — with their definitions, denominators and data coverage.

> This is an operational analytics module, not an employee surveillance or
> scoring system. It never combines attendance, assignment, machine running
> time, AI-observed activity, production counts and overtime into one score;
> never ranks individual workers by camera activity; and is never a basis for
> pay, deductions or discipline.

## 1. Architecture

```text
attendance_sessions · break_sessions · worker_shift_assignments · shifts · overtime_requests      ┐
workstations · workstation_assignments · worker_profiles                                         │  existing modules
production_counter_events · production_corrections · production_plans (+ calendar, styles)      │  (no new tables,
machines · machine_state_transitions · machine_states · machine_telemetry · devices · heartbeats │   no duplicated logic)
monitoring_events · monitoring_alerts · worker_track_associations · cameras                      ┘
        │  existing scope helpers (factory / line reach) + validated filters + REAL|SIMULATED
        ▼
AnalyticsContext ──► Overview · Production · Machine · Workforce · Activity · Insights · Reports services
        │                         (aggregation in SQL; formulas in pure, unit-tested rules)
        ▼
/api/analytics/*  ──►  Analytics section (/app/analytics/*) · CSV / PDF exports (audited)
```

| Layer | Files |
|---|---|
| Application (pure) | `Analytics/MetricCatalog.cs` (metric definition layer), `Analytics/AnalyticsRules.cs` (periods, KPIs, machine availability, attendance aggregation, overtime, confidence bands), `Analytics/InsightRules.cs` (rule engine), `Analytics/ReportModel.cs` (report document + CSV), `Dtos/Analytics`, `Abstractions/Analytics.cs` |
| Infrastructure | `Analytics/AnalyticsContext.cs` (scope, filters, period, scoped queries), `ProductionAnalyticsService`, `PlanTargets`, `MachineAnalyticsService`, `WorkforceAnalyticsService`, `ActivityAnalyticsService`, `OverviewAnalyticsService`, `OperationalInsightsService`, `AnalyticsReportService`, `PdfReportWriter` |
| API | `Controllers/AnalyticsController.cs` |
| Frontend | `/app/analytics` (overview), `/production`, `/workforce`, `/machines`, `/activity`, `/insights`, `/reports`; `components/analytics/shared.tsx`, `lib/analytics.ts` |

**Reused:** the attendance module's scoping and the scheduled / absent rule;
the IoT module's stored pieces (counting rules applied at ingestion), time-by-state
calculation (`IotMetrics.Durations`) and machine reach; the production module's
plan schedule (`ProductionReadModel.Schedule`, `PlanSchedule.PlannedBetween`) for
targets; monitoring reach rules; the audit log; the existing CSV escaping.

**Aggregation strategy.** Group-bys run in PostgreSQL (EF LINQ; parameterised
SQL where the activity label lives in the JSON payload). Rows are loaded into
memory only where a per-row rule is needed and bounded: hourly production
(ranges ≤ 3 days), attendance sessions (one small row per worker-day,
≤ 300 000), machine state transitions of the machines in scope (≤ 1 000
machines). Ranges are limited to 93 days. No summary tables or materialized
views were added: every figure is computed from the source records at
request time, so late data, corrections and duplicates are always reflected
correctly; the indexes added by the IoT and attendance modules cover the
queries. A read model can be added later if volumes require it.

## 2. Filters (every page and report)

`factoryId`, `departmentId`, `floorId`, `lineId`, `workstationId`, `machineId`,
`shiftId`, `from` / `to` (factory-local dates `yyyy-MM-dd`; default today; a
running day ends now), `source` (`REAL` default | `SIMULATED` — never both),
`eventSource` (`DEVICE` | `MANUAL` | `SIMULATOR`: how records arrived),
`family` (AI: `generic` | `garment`).

Every organisational filter is validated against the caller's reach and the
chosen factory (another factory's line → 400 `line_not_in_factory`; another
factory → 403 `factory_out_of_scope`; a supervisor asking for another line →
403 `line_out_of_scope`). Days follow the factory's time zone (attendance
policy, default **Asia/Dhaka**); timestamps are stored in UTC.

| Filter | Production | Machines | Workforce | AI activity |
|---|---|---|---|---|
| department / floor | counter's workstation | machine's workstation | worker | event |
| workstation | ✓ | ✓ | workers assigned there | event |
| machine | ✓ | ✓ | — | — |
| shift | factory shift of the count | not split by shift (warning) | session / schedule | — |
| eventSource | MQTT+HTTP / MANUAL / SIMULATOR transport | — | check-in source | producer |

## 3. Data-source mapping and KPI rules

Every KPI references a metric id; the full definitions (sources, formula,
window, inclusions, exclusions, denominator, missing data, kind) are served
at `GET /api/analytics/metrics` and printed in every report. The essentials:

| KPI | Kind | Source | Formula | Denominator | Missing data |
|---|---|---|---|---|---|
| Registered workers | measured | worker_profiles | active workers in scope | — | 0 |
| Scheduled workers | derived | worker_shift_assignments, shifts | worker-days with an assignment in force whose shift works that weekday | — | "no schedule data", never zero absence |
| Present | measured | attendance_sessions | distinct worker × day with a check-in | — | 0 |
| Attendance rate | derived | sessions + schedules | scheduled workers who checked in ÷ scheduled workers whose shift started | scheduled, started | unavailable (not 0 %) |
| Absent | derived | sessions + schedules | scheduled, shift start + grace passed, no session | — | unavailable without schedules |
| Late / early departure | measured | attendance_sessions | sessions flagged IsLate / IsEarlyDeparture (distinct worker-days) | present | 0 |
| On break now | measured | break_sessions | active breaks of open sessions | — | 0 |
| Approved / actual overtime | measured | overtime_requests | Σ ApprovedMinutes / Σ ActualMinutes of Approved, InProgress, Completed | approved (for actual ÷ approved) | 0 |
| Assignment coverage | derived | workstation_assignments | active workers with an active assignment ÷ active workers | active workers | unavailable |
| Production output | measured | production_counter_events, production_corrections | Σ stored pieces + Σ corrections | — | "no counter data" |
| Planned / achievement | derived | production_plans | plan schedule's planned pieces in the range; counted ÷ planned | planned | not shown without a plan |
| Machine running | measured | machine_state_transitions | time in RUNNING | — | 0 |
| Uptime | derived | transitions | running ÷ (running + idle + stopped + error) | confirmed-state time | unavailable |
| Confirmed downtime | measured | transitions | stopped + error | confirmed-state time | — |
| Data gap / coverage | derived | transitions | offline + unknown; confirmed ÷ (period − maintenance) | period − maintenance | — |
| Machine errors | measured | transitions | transitions into ERROR | — | 0 |
| Open alerts | measured | monitoring_alerts | OPEN + ACKNOWLEDGED + ESCALATED | — | 0 |
| AI predictions / uncertain share | AI estimate | monitoring_events | predictions per label; uncertain ÷ all | all predictions | unavailable |
| Tracks / observation hours | AI estimate | monitoring_events | distinct (camera, track); Σ per-track (last − first), ≤ 12 h each | — | 0 |
| Verified tracks / workers | derived | worker_track_associations | VERIFIED associations during the range | tracks observed | 0 |

**No double counting.** A counter message is stored once per device +
message id (MQTT redelivery, HTTP retry or both transports count once);
resets, baselines, out-of-order and implausible readings add 0 pieces; a
worker counts once per day however many sessions; tracks are counted
distinct per camera; overlapping sessions are per worker-day.

**Kept independent, never combined:** attendance duration · workstation
assignment · machine running time · AI-observed activity · verified
production count · approved overtime.

**Machine time.** A missing heartbeat is OFFLINE / UNKNOWN time — a data gap,
never downtime. Maintenance is excluded from uptime and coverage. A RUNNING
machine doesn't prove anyone is operating it.

**Workforce concepts** are reported side by side: scheduled worker-days ·
worker-days with a check-in · workers assigned to a workstation · workers
with a verified camera identity · camera tracks detected (not people, not
workers, never attendance) · tracks with uncertain activity.

## 4. Operational insights

Rule-based (`InsightEngine`), evaluated on request for the filters. Each
insight has: type, factory and scope (line / machine), detection time,
evidence (values with metric ids), data sources, severity, reliability (with
the reason), a recommended investigation, and a caveat.

| Rule | Fires when | Notes |
|---|---|---|
| LINE_BELOW_TARGET | counted < 85 % of planned (≥ 20 planned pieces); high < 60 % | only with an active plan; reliability from counter coverage on the line |
| HIGH_MACHINE_DOWNTIME | confirmed downtime > 25 % of ≥ 1 h confirmed-state time | offline time excluded |
| REPEATED_MACHINE_ERRORS | ≥ 3 errors (high ≥ 6) | |
| COUNTER_INACTIVITY | machine with a counter ran ≥ 1 h with no counted piece | |
| ATTENDANCE_SHORTAGE | < 85 % of ≥ 5 scheduled worker-days checked in; or present < 90 % of planned operators | leave not modelled |
| ASSIGNMENT_GAP | active workstations without an assignment | |
| HIGH_OVERTIME | actual overtime > 15 % of attendance time, or > 110 % of approved | warnings for review, not legal advice |
| LOW_DATA_COVERAGE | devices online < 70 %, machine coverage < 60 %, or active cameras without data | |
| REPEATED_ALERTS | ≥ 5 occurrences of one alert type / subject in the period | |

Rules never claim a root cause ("low output is not evidence that workers
underperformed"). A rule whose data the caller can't see is listed as *not
checked* with the reason.

## 5. Reports and exports

| Type | Content | Needs |
|---|---|---|
| `daily-operations` / `weekly-operations` / `monthly-operations` | 1 / 7 / 30 days ending on `to` (today): overview, coverage, production, machines, attendance, overtime, AI observations, insights | any analytics permission; sections you can't see are left out and listed |
| `production` | KPIs, by line / shift / machine / workstation / department / floor, daily, planned vs actual, counter quality | production.view or monitoring.view |
| `machines` | KPIs, line availability, per machine, daily | monitoring.view, devices.view or production.view |
| `attendance` | KPIs, daily, by line / department / shift, separate concepts | attendance.view |
| `overtime` | summary, daily | overtime.view or attendance.view |
| `device-health` | devices online, per device health, heartbeats, offline and error events | devices.view or monitoring.view |
| `ai-activity` | predictions, confidence, cameras, identity coverage, disclaimer | monitoring.view |

Every report includes the date range, factory / organisational scope,
generation time and requester, applied filters, metric definitions, REAL /
SIMULATED indication and missing-data warnings. `GET /api/analytics/reports/{type}`
returns it as JSON (preview); `…/export?format=csv|pdf` as a file. Exports run
through the same scope and filters, so an export can never contain another
factory's data. CSV cells are escaped (formula-injection guard; numbers stay
numeric). PDFs use the standard Helvetica font (Latin text; other scripts,
e.g. Bengali names, are replaced — the CSV keeps them). Generation and export
are written to the audit log (`analytics.report_generated`,
`analytics.report_exported`) with type, scope, period, source and filters.

## 6. API

| Method | Path | Permission (any of) |
|---|---|---|
| GET | `/api/analytics/metrics` | production.view, monitoring.view, devices.view, attendance.view, overtime.view, workers.view, reports.view |
| GET | `/api/analytics/overview` | same |
| GET | `/api/analytics/production` | production.view, monitoring.view |
| GET | `/api/analytics/machines` | monitoring.view, devices.view, production.view |
| GET | `/api/analytics/workforce` | attendance.view, overtime.view, workers.view |
| GET | `/api/analytics/activity` | monitoring.view |
| GET | `/api/analytics/insights` | any analytics permission (rules filtered) |
| GET | `/api/analytics/reports` | any (each type says whether you may generate it) |
| GET | `/api/analytics/reports/{type}` | per type (audited) |
| GET | `/api/analytics/reports/{type}/export?format=csv\|pdf` | per type (audited) |

Example — `GET /api/analytics/production?factoryId=…&from=2026-11-15&to=2026-11-15`:

```json
{
  "scope": { "scope": "MAIN · Main factory", "from": "2026-11-15", "to": "2026-11-15", "timeZone": "Asia/Dhaka", "source": "REAL",
             "filters": [{ "name": "Factory", "value": "MAIN · Main factory" }], "sections": ["production", "machines", "..."] },
  "kpis": [
    { "metricId": "production.pieces", "label": "Production output", "value": 1240, "unit": "pieces", "kind": "MEASURED", "availability": "OK" },
    { "metricId": "production.achievement", "label": "Plan achievement", "value": 82.7, "unit": "%", "numerator": 1240, "denominator": 1500,
      "kind": "DERIVED", "availability": "OK" }
  ],
  "byLine": [{ "key": "line-3", "label": "Line 3", "pieces": 1240, "sharePct": 100, "piecesPerHour": 155, "planned": 1500, "achievementPct": 82.7 }],
  "planVsActual": [{ "lineId": "line-3", "lineName": "Line 3", "planned": 1500, "actual": 1240, "achievementPct": 82.7, "plans": 1 }],
  "quality": { "countEvents": 1251, "resets": 1, "excluded": 1, "suspectJumps": 0, "corrections": 0,
               "duplicateHandling": "A counter message is stored once per device + message id ..." },
  "warnings": []
}
```

Example — a KPI without data: `{ "metricId": "attendance.rate", "value": null, "availability": "NO_DATA", "note": "Unavailable (not 0 %) when nobody is scheduled." }`;
a KPI hidden by role: `{ "metricId": "production.pieces", "value": null, "availability": "NOT_PERMITTED", "note": "Needs production.view or monitoring.view." }`.

## 7. Permission matrix

| Role | Overview | Production | Machines | Workforce | AI activity | Insights / reports |
|---|---|---|---|---|---|---|
| Super Admin | all factories | ✓ | ✓ | ✓ | ✓ | all types |
| Factory Admin | own factories | ✓ | ✓ | ✓ | ✓ | all types |
| Factory Manager | own factories | ✓ | ✓ | ✓ | ✓ | all types |
| Production Supervisor | own lines | own lines | own lines | own lines | own lines | own lines |
| HR / Attendance Officer | workforce + overtime | — | — | ✓ | — | attendance, overtime, operations (workforce sections) |
| Monitoring Operator | production, machines, attendance, alerts, AI | ✓ (monitoring.view) | ✓ | attendance, assignments (no overtime) | ✓ | per data |
| IT / Device Administrator | machines, devices, alerts | — | ✓ | — | — | machines, device health |
| Worker | — | — | — | — | — | — |

All checks run in the backend (endpoint permission + per-section checks +
scoped queries); hidden sections are never computed.

## 8. Limitations

* Leave and holidays are not modelled: scheduled workers without a session count as absent (flagged).
* Planned vs actual needs active production plans; plans follow the production calendar's shifts, so it isn't shown with a factory-shift, machine, workstation or event-source filter, or for SIMULATED data. Counter pieces must count the same unit as the plan.
* Machine time is not split by factory shift.
* Hourly production for ranges ≤ 3 days; all ranges ≤ 93 days.
* Insights are computed on request (not stored or notified); thresholds are constants in `InsightRules.cs`.
* PDFs: standard fonts only (non-Latin characters replaced).
* Workforce staffing shortages use planned operators of active production plans; there is no separate staffing-requirement table.
* The database-backed tests need Docker (Testcontainers).

## 9. Setup

Nothing to configure: the module reads existing data. Open **Factory
analytics** in the sidebar (or `/app/analytics`). To see figures, the
underlying modules need data: attendance (punches, shift assignments),
IoT counters / machine sensors (or the IoT simulator with *Data → Simulated*),
camera AI events, production plans for targets.

```bash
cd BunonBrain.Backend && dotnet test BunonBrain.Api.Tests      # unit tests; DB tests run when Docker is available
```
