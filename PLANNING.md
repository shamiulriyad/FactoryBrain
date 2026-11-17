# Production planning & target management

Production orders, garment styles, allocation of orders to lines, targets, capacity
estimates, daily plans, planned-versus-actual from the IoT production counters,
exceptions and reports. It **extends** the production module and the IoT and analytics
modules; it does not duplicate factories, lines, shifts, machines, workers or the
production counter events.

- [1. Architecture](#1-architecture)
- [2. Order lifecycle](#2-order-lifecycle)
- [3. Styles and operations](#3-styles-and-operations)
- [4. Allocation rules](#4-allocation-rules)
- [5. Targets](#5-targets)
- [6. Capacity estimation](#6-capacity-estimation)
- [7. Daily plans](#7-daily-plans)
- [8. Production counter integration](#8-production-counter-integration)
- [9. Exceptions](#9-exceptions)
- [10. Reports](#10-reports)
- [11. API](#11-api)
- [12. Permission matrix](#12-permission-matrix)
- [13. Time, scope and audit](#13-time-scope-and-audit)
- [14. Known limitations](#14-known-limitations)
- [15. Setup and operation](#15-setup-and-operation)

---

## 1. Architecture

| Layer | What it holds |
|---|---|
| Domain `Planning.cs` | Allocations, targets (+ revisions), daily plans (+ items, events), exceptions, line output points, capacity settings, order status history. `Production.cs` gained the order and style fields. |
| Application `Planning/` | Pure rules, tested without a database: `PlanningCatalog` (lifecycles, target scopes, precedence), `PlanningMath` (shift time, planned vs actual, delivery forecast), `CapacityEstimator`, `ExceptionRules`. |
| Infrastructure `Planning/` | `AllocationService`, `TargetService`, `DailyPlanService`, `CapacityService`, `PlanningMonitorService`, `ExceptionService` (+ `PlanningExceptionWorker`), `PlanningReportService`, `PlanningSetupService`; `PlanningActuals` reads the counters. |
| Api | `PlanningController` (`/api/planning`); orders and styles stay on `ProductionController`. |
| Frontend | `/app/planning/*` (monitoring, orders, styles, allocations, daily plans, targets, capacity, exceptions, reports, setup). |

Planning records are separate from the append-only counter events: a plan says what was
intended, the counters say what was counted. Nothing here writes to counter events.

Reused as they are: `Factory`, `ProductionLine`, `Department`, `ProductionFloor`, `Shift`
(factory shifts with breaks), `Workstation`, `Machine`, `Device`, `ProductionCounterEvent`,
`ProductionCorrection`, `MachineState`, `WorkstationAssignment`, the analytics report
document and its CSV / PDF writers, the audit log.

## 2. Order lifecycle

`ProductionOrder` gained: factory, buyer order reference, product description and
category, start date, planned completion date, priority (`LOW` `NORMAL` `HIGH` `URGENT`)
and status reason. `DueDate` is the **delivery deadline**.

```
DRAFT ──► PLANNED ──► IN_PROGRESS ──► COMPLETED
  │          │  ▲          │  ▲
  │          ▼  │          ▼  │
  │        ON_HOLD ◄───────┘  │ (ON_HOLD → PLANNED / IN_PROGRESS)
  └────────► CANCELLED ◄──────┘   (from any state before COMPLETED)
```

- Required to create: order number (unique), style, quantity (1–10,000,000), delivery
  deadline, factory (defaulted when the caller has exactly one). A planned order also needs
  a buyer; without one it is saved as a **draft** and can't be planned or allocated.
- Dates: planned completion ≥ start; start ≤ delivery deadline. A planned completion after
  the deadline is allowed (and shows up as a delay), not hidden.
- Putting an order on hold or cancelling needs a reason. Cancelling or completing closes
  the order's open allocations and resolves its open exceptions. Final states have no way out.
- Every change is a row in `production_order_status_changes` (append-only, database
  trigger) with who, when and why, and an audit-log entry.
- Wire statuses are snake_case (`in_progress`). The old values still parse: `open` means
  in progress (and as a list filter, planned + in progress), `closed` means completed
  (an edit that closes a planned order passes through in progress).
- Orders recorded before this module: `Open` → `InProgress`, `Closed` → `Completed`
  (migration), and the factory is set when the installation has exactly one.
- Attachments are not stored here; buyer documents and tech packs live under
  Merchandising.

## 3. Styles and operations

`ProductionStyle` gained garment category, description, size range, colour variants,
complexity (`LOW` … `VERY_HIGH`, optional) and quality checkpoints. The operation sequence
(`StyleOperation`: sequence, name, SMV, machine type, predecessors) already existed; the
machine types a style needs are derived from it. Everything but code and name is optional —
styles differ and nothing is assumed. The style's SMV is the approved value, else the sum
of its operations, else missing (which makes capacity incomplete).

## 4. Allocation rules

An allocation puts a quantity of an order on one line for a date range.

- Only planned or in-progress orders with a factory can be allocated; the line must belong
  to the order's factory and be active; start ≤ end.
- **Limit:** counting allocations (planned, active, completed) of an order may not exceed its
  quantity. The check and the write run under a per-order advisory lock, so two
  simultaneous allocations can't both pass.
- **Override:** a factory-level user may save beyond the quantity with
  `allowOverAllocation` and a reason; the allocation is flagged `overAllocated` with who and
  why, and an *excess allocation* exception stays open (low severity when overridden).
  Supervisors cannot override.
- The same order cannot be on the same line in overlapping dates (change that allocation).
  Different orders may share a line; capacity shows the conflict.
- Edits need a reason and the current version; order and line can't change (cancel and
  re-allocate). Statuses move `PLANNED → ACTIVE → COMPLETED`, or `CANCELLED` (reason).
  Each change is in `production_allocation_changes` (append-only).
- A responsible supervisor may be named: an active user of the line's factory who reaches
  the line.

## 5. Targets

A target has a **level** (`FACTORY`, `DEPARTMENT`, `FLOOR`, `LINE`, `SHIFT`, `WORKSTATION`,
`MACHINE`), a **period** (`DAILY`, `SHIFT`, `HOURLY`), a quantity, an effective date range,
and optionally a shift, an order or an allocation (line level). Each level carries exactly
the ids it needs.

- **Versions, not edits.** Revising adds a version (same target key, next revision) that
  starts on its effective date; the previous version's `EffectiveTo` becomes the day
  before. Revisions can't start in the past or on/before the current version's start.
  Database indexes allow one version in force and one pending revision per target.
  Every create / revise / approve / reject / end is a row in `production_target_revisions`
  (append-only) with who, when, why and the old and new quantity.
- **Approval:** factory-, department- and floor-level targets (and their revisions) are
  `PENDING` until someone with `planning.plans.approve` — not the creator — decides; a
  rejection needs a reason. Until a revision is approved the previous version stays in force.
  Line-level and below apply immediately.
- **Which target applies** to a line, shift and day: the most specific wins — allocation,
  then order, then shift-specific, then the general line target. A plan item can take its
  quantity from it: shift targets as they are, daily targets split by the shift's share of
  productive minutes, hourly targets × the shift's productive hours.
- Supervisors set targets only for their own lines (and not at factory, department,
  floor or shift level).
- Performance of a target on a date uses the line's output counters (§8); a machine or
  workstation target compares with that machine's own counter and is labelled as an
  operation count, not finished garments. Hourly targets compare with the pace over
  productive hours so far.

## 6. Capacity estimation

```
capacity (pieces) = Σ working days (productive minutes × operators × efficiency ÷ 100) ÷ SMV
utilisation       = (planned + other commitments) ÷ capacity
shortage          = max(0, planned + other commitments − capacity)
```

| Input | Source |
|---|---|
| Productive minutes | The factory's active shifts that work each weekday: start to end minus **every** scheduled break (paid or not — machines are idle). **Assumption:** every active shift runs on the line; narrow it through the daily plan's shift assignments. |
| SMV | Style: approved SMV, else sum of operation SMVs. |
| Efficiency | `line_capacity_settings` — the plant's own assumption. **No default.** |
| Operators | `line_capacity_settings.PlannedOperators`, else workers currently assigned to the line's workstations. |
| Commitments | Other planned / active allocations on the line, prorated over their own working days that overlap the range. |
| Machines | Style operation machine types against the line's active machines and their state (ERROR / maintenance = unavailable). Names are matched exactly (ignoring case, spaces, hyphens) or by containment for names of four+ letters; otherwise `NOT_FOUND`, `UNVERIFIED` (no machine on the line has a type) or `UNAVAILABLE` (all faulty). |

If SMV, efficiency, operators or scheduled time is missing the estimate is **`INCOMPLETE`**:
no capacity, utilisation or shortage number is returned, and each gap says how to fix it.
Responses are labelled `isEstimate`, list every assumption with its source, and warn about
operators beyond the line's workstations or efficiency above 100 %. A capacity-shortage
exception is raised only from a complete estimate.

## 7. Daily plans

One plan per factory and date (dates from today to 60 days ahead). Statuses:

```
DRAFT ─submit→ UNDER_REVIEW ─approve→ APPROVED ─publish→ PUBLISHED ─(revision published)→ REVISED
  ▲                │ return              │ return                └ cancel                       CANCELLED
  └────────────────┴─────────────────────┘
```

- An **item** is order · line · shift · target quantity (typed, or from the configured
  target), unique per plan, line and shift. It must be backed by an open allocation of that
  order on that line covering the date, the order must be planned or in progress, the line
  active and the shift active and working that weekday.
- Items are checked again at submit, approve and publish (orders, allocations and shifts may
  have changed). Draft items show capacity warnings (target above the shift's estimated
  capacity, or why capacity can't be assessed).
- Submitter ≠ approver (`PRODUCTION_REQUIRE_SEPARATE_APPROVER`, default on). Publishing
  needs `planning.plans.publish`. Plans for past dates can't be created or published.
- **Revising a published plan** copies it to a new draft revision (reason required); the
  published plan stays in force until the revision is published, which marks the old one
  `REVISED`. Database indexes guarantee one published and one open plan per date.
- Every step is a row in `daily_plan_events` (append-only) and in the audit log.
- Visibility: people who can only follow the plan (monitoring operators) and line-scoped
  supervisors see only `PUBLISHED` / `REVISED` plans, supervisors only the items of their
  own lines.

## 8. Production counter integration

Actual production comes from the existing IoT records; planning never writes to them.

1. **Only designated output counters count.** A line has many machine counters counting
   different operations, so summing them would overstate garments. A factory manager
   designates the counter device(s) that count finished garments leaving a line
   (`line_output_points`; production-counter devices only, not a machine that counts
   cycles). Without one, the line's output can't be verified.
2. **Verified actual** for an item = pieces of those devices on that line in the item's
   shift and production date (`ProductionCounterEvent.Pieces`, which already applies the
   counting rule and counter resets) plus audited `ProductionCorrection`s to those counters or
   to the line. Each message is stored once per device and message id, so duplicates and
   resends are not counted twice.
3. **No data is not zero.** Counter alive (heartbeats in the shift) but no pieces → 0.
   No counter data at all → the actual is empty and the item is `NO_DATA`.
4. **Recorded actual:** where a line has no output counter, good sewing-output records
   entered by people (the production module) for that order, line, date and shift are used
   and labelled `RECORDED`, never `verified`. The two are never added together. Simulated
   runs ignore manual records.
5. **REAL and SIMULATED are separate** everywhere (`source=`). Simulated devices are never
   output points of a real view and vice versa.
6. AI activity classification is not production and is never read.
7. **Order completion** = output in shifts that a published plan gave the order ÷ order
   quantity. Output outside published plans is unattributed (the counters can't say which
   order was on the line).

Planned vs actual: expected-by-now is the shift target spread evenly over productive
minutes (breaks excluded, overnight shifts belong to the date they start, instants in UTC,
dates in the factory's time zone). `BEHIND` below 90 % of expected (tolerance 10 %), `AHEAD`
above 105 %; `COMPLETE` / `MISSED` after the shift. A guide, not a promise.

**Delivery forecast:** the mean of the last (up to) 7 production days with verified output
(at least 2 needed, else `INSUFFICIENT_DATA`) against the working days left before the
deadline. Overdue orders with pieces left are `OVERDUE`.

## 9. Exceptions

Rule-based, from stored records only. Each carries the order and line, detection time,
supporting data, severity, reason, suggested investigation, and acknowledge / resolve
status. A detection key allows one open exception; re-detection refreshes it, and a
condition that clears closes it (resolved by `system`). People resolve with a note.

| Type | Raised when |
|---|---|
| `OUTPUT_BEHIND_TARGET` | A live order's published item is behind (after ≥ 60 productive minutes) or its shift ended short. HIGH below 60 % of expected, MEDIUM below 80 %. |
| `DELIVERY_AT_RISK` | The forecast needs more working days than are left, or the deadline passed. HIGH when overdue or ≤ 2 days left. |
| `LINE_UNAVAILABLE` | A line with published work today is deactivated, or every monitored machine is in error / maintenance / offline. |
| `CAPACITY_SHORTAGE` | A complete capacity estimate of an upcoming allocation is short. |
| `MISSING_COUNTER_DATA` | After ≥ 30 productive minutes the line has no output counter (LOW) or its counter sent nothing in the shift (MEDIUM). |
| `EXCESS_ALLOCATION` | An order's allocations exceed its quantity. |
| `PRODUCTION_INTERRUPTION` | Machines stopped / in error for ≥ 10 productive minutes (breaks excluded) during a running shift. |

None names a worker or infers a cause; each suggests what to check. The check runs on
demand (`POST /api/planning/exceptions/evaluate`, factory level) and every 10 minutes for
factories with a published plan (`PlanningExceptionWorker`, REAL data only; not started
in the Testing environment).

## 10. Reports

Built as the analytics module's report documents (header, scope, period, source, filters,
warnings, metric definitions) and exported through the same CSV and PDF writers. Types:
`order-production`, `line-target-achievement`, `daily-plan-vs-actual`,
`shift-plan-vs-actual`, `delivery-progress`, `capacity-utilization`, `production-delay`,
`target-revisions`. Reading needs `planning.view`, exporting `planning.reports.export`;
supervisors get their own lines. Exports are audited (`planning.report_exported`). Ranges
are limited to 62 days.

## 11. API

All under `/api/planning` unless noted. Writes need the current `expectedVersion`.

| Method & route | Permission | Purpose |
|---|---|---|
| `GET factories`, `GET context?factoryId` | view | Factories in reach; shifts, lines, floors, departments, capabilities |
| `GET/PUT lines/{id}/capacity-settings` | view / setup | Efficiency and operator assumptions |
| `GET/POST output-points`, `DELETE output-points/{id}` | view / setup | Counters that measure finished output |
| `GET allocations`, `GET allocations/{id}`, `GET allocations/{id}/capacity` | view | List, detail with history, capacity |
| `POST allocations`, `PUT allocations/{id}`, `POST allocations/{id}/status` | allocations.manage | Allocate, change, cancel |
| `GET orders/{id}/allocations`, `GET orders/{id}/progress` | view | Order allocation summary; output, completion, forecast |
| `GET targets`, `GET targets/{id}`, `GET targets/effective` | view | List (date, history, performance), versions + revisions, the target in force |
| `POST targets`, `POST targets/{id}/revise`, `POST targets/{id}/end` | targets.manage | Create, revise, end |
| `POST targets/{id}/decision` | plans.approve | Approve / reject a pending target |
| `GET capacity`, `GET capacity/overview` | view | What-if estimate; per-line utilisation |
| `GET daily-plans`, `…/for-date`, `…/{id}` | view | Plans (drafts only for managers) |
| `POST daily-plans`, `PUT …/{id}`, `POST/PUT/DELETE …/{id}/items[/{itemId}]` | plans.manage | Create, notes, items |
| `POST …/{id}/submit`, `…/revise`, `…/cancel` | plans.manage | Workflow |
| `POST …/{id}/approve`, `…/return` | plans.approve | Workflow |
| `POST …/{id}/publish` | plans.publish | Workflow |
| `GET monitor` | view | Dashboard |
| `GET exceptions[/{id}]` | view | List, detail |
| `POST exceptions/evaluate`, `…/{id}/acknowledge`, `…/{id}/resolve` | exceptions.review | Check, acknowledge, resolve |
| `GET reports`, `GET reports/{type}` | view | Types; a report |
| `GET reports/{type}/export?format=csv\|pdf` | reports.export | Download |
| `/api/production/orders` `GET` `POST` `PUT` | production.view or planning.view / production.manage or planning.orders.manage | Orders (now factory-scoped) |
| `POST /api/production/orders/{id}/status`, `GET …/transitions` | as above | Lifecycle and history |
| `/api/production/styles` | production.view or planning.view / production.setup or planning.styles.manage | Styles |

Errors are problem details with a stable `code` (e.g. `allocation_exceeds_order`,
`version_conflict`, `same_approver`, `no_allocation`, `target_not_current`).

## 12. Permission matrix

| Permission | Super / Factory Admin | Factory Manager | Production Supervisor | Monitoring Operator |
|---|---|---|---|---|
| `planning.view` | ✓ | ✓ | their lines | published plans |
| `planning.orders.manage`, `.styles.manage`, `.allocations.manage`, `.plans.manage`, `.plans.approve`, `.plans.publish`, `.setup` | ✓ | ✓ | | |
| `planning.targets.manage` | ✓ | ✓ | their lines | |
| `planning.exceptions.review`, `planning.reports.export` | ✓ | ✓ | their lines | |

HR, IT and Worker roles have none. The server enforces every row (see
`EndpointAccessMatrixTests`); the UI only hides what the API would refuse.

## 13. Time, scope and audit

- Timestamps are UTC. Operational dates (plan date, production date, effective dates) are in
  the factory's time zone (its attendance policy, default `Asia/Dhaka`), resolved through
  `AttendanceRules.ResolveTimeZone` (the API runs with invariant globalization).
- Scope: factory (`AccessContext.CanAccessFactory`) and line (`CanAccessLine`) on every
  query and write; another factory's id is 403 or 404, never data.
- Audit: every write adds an audit-log row with the factory; histories (order status,
  allocation changes, target revisions, plan events) are append-only tables.

## 14. Known limitations

- Output is attributed to an order only through published plan items; counters cannot tell
  which order was on the line. Unplanned output is counted on lines and targets but not on
  orders.
- Verified output needs a designated output counter per line. Until configured, lines show
  "no data" (or recorded output) — by design.
- Capacity assumes all active shifts run on the line, a single efficiency per line (not per
  style or learning curve), and operators from settings or assignments. It is an estimate.
- Machine-type matching is by name; differing names show as `NOT_FOUND` rather than a
  false shortage.
- Hourly targets are compared as a pace over the productive hours so far.
- The exception worker checks plans for today and yesterday only; it polls (no push).
- Order attachments are not supported here (Merchandising holds buyer documents).
- The order status vocabulary changed (`open` / `closed` accepted as aliases).
- The Docker-based suite `DbPlanningTests` could not be run on the development machine
  (no Docker engine with enough memory); its rules were checked on Supabase in rolled-back
  transactions and the logic by `PlanningUnitTests`.

## 15. Setup and operation

1. Migrations `AddPlanningTargetManagement` and `FixPlanningUniqueIndexes` apply at API
   start. They convert order statuses and add the planning tables and permissions.
2. As a factory manager: add styles (SMV or operations), designate each line's output
   counter and set its efficiency (Planning → Setup).
3. Register orders, allocate them to lines, set targets, then build, review, approve and
   publish the daily plan. Watch Monitoring; run or await the exception check.

Tests: `PlanningUnitTests` (86 pure-logic tests), `EndpointAccessMatrixTests` (permissions),
`DbPlanningTests` (`[DockerFact]`, real PostgreSQL via Testcontainers).
