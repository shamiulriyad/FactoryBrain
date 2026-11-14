# Access control (users, roles, permissions)

BunonBrain uses role-based access control with factory and production-line
scope. Every decision is made **on the server**; the frontend only hides what
the server would refuse.

- **Source of truth:** `BunonBrain.Backend/BunonBrain.Application/Access/AccessCatalog.cs`
  (permissions, the eight system roles, which role grants what, who may grant whom).
- **Enforcement:** `[RequirePermission(...)]` on every controller action
  (`BunonBrain.Api/Authorization/PermissionAuthorization.cs`), plus scope
  rules inside the services (`BunonBrain.Infrastructure/Access/*`).
- **Tests:** `EndpointAccessMatrixTests` (every endpoint's guard, every role
  against every endpoint), `RoleMatrixTests` (the matrix itself) and
  `DbAccessControlTests` (end to end on PostgreSQL).

## 1. Model

| Concept | Table | Notes |
|---|---|---|
| Login account | `users` | Email, PBKDF2 password hash, full name, phone, `IsActive`, `MustChangePassword`, `SecurityStamp`, last sign-in. |
| Role | `roles` | The eight system roles (projection of the catalog, `IsSystem = true`). Not editable through the API. |
| Permission | `permissions` | Dotted codes grouped by module. `PlatformOnly` ones can only ever reach Super Admin; `Reserved` ones exist for modules not built yet. |
| Role → permissions | `role_permissions` | Synced from the catalog at startup. |
| User → roles | `user_roles` | A user may hold several roles; permissions add up. |
| User → factories | `user_factory_assignments` | A factory-level role acts only inside these factories. |
| User → lines | `user_production_line_assignments` | A Production Supervisor acts only on these lines (they must belong to the user's factories). |
| Factory | `factories` | Tenant. Created by a Super Admin. Deactivating one removes it from every member's access at once. |
| Production line | `production_lines.FactoryId` | Lines are attached to a factory. |
| Worker | `workers` | **Separate from login accounts.** A worker needs no login; at most one account can be linked (`UserId`) so the worker sees their own records. Permanent Worker ID (`W-001`) from the `worker_number_seq` sequence; never deleted, never reused. |
| Worker status history | `worker_status_history` | Every status change (Active, Inactive, Suspended, Resigned, Terminated) with reason, effective date and who. |
| Worker card | `worker_cards` | RFID / NFC credential. One *active* card per UID; returned cards can be re-issued, past assignments are kept. |
| Department, production floor | `departments`, `production_floors` | Per-factory reference data for worker placement; deactivated, never deleted. |
| Workstation | `workstations` | A table on one production line (e.g. `T-025`), with department, optional floor, position and a configurable **capacity** (1–20 workers). Deactivated, never deleted. |
| Machine | `machines` | A machine of a factory; stands at one workstation at a time, or nowhere yet. |
| Workstation assignment | `workstation_assignments` | One worker at one workstation from `StartAt` to `EndAt` (open while active), with the line / department / floor / machine copied at the time, who assigned and ended it, reasons, and the assignment a transfer replaced. Never deleted or reopened. A recorded placement, **not** proof of presence. |
| Audit | `audit_log` (+ `FactoryId`) | Append-only (the database rejects UPDATE/DELETE/TRUNCATE). Factory-scoped readers see only their factories' entries. |

Migration: `20261112165317_AddAccessControl`. It renames the old
`users.Role` column to `LegacyRole`; at startup `AccessSeeder` maps it once:
**Admin → Super Admin**, **Operator → Factory Manager + Production Supervisor**
(in a default factory `MAIN`), **Viewer → no role** (none of the eight roles is
a read-only view of every module, so the account keeps its sign-in and an
administrator chooses its role). Each mapping is audited (`user.role_migrated`).
Lines that existed before factories are attached to `MAIN` when it is created.

## 2. Roles and reach

| Role | Reach | Granted by | Purpose |
|---|---|---|---|
| Super Admin | Platform (all factories) | Super Admin | Factories, Factory Admins, platform settings, global audit |
| Factory Admin | Assigned factories | Super Admin | Users and roles, lines, departments, factory settings |
| Factory Manager | Assigned factories | Super / Factory Admin | Operations oversight, production, approvals, reports |
| Production Supervisor | Assigned **lines** | Super / Factory Admin | Workers and workstations on their lines, production, line alerts |
| Monitoring Operator | Assigned factories | Super / Factory Admin | Monitoring room, camera monitoring, event timeline and reviews, acknowledge and escalate alerts, monitoring simulator |
| HR / Attendance Officer | Assigned factories | Super / Factory Admin | Worker records, attendance, shifts, leave, overtime |
| IT / Device Administrator | Assigned factories | Super / Factory Admin | Cameras, devices, integrations, technical logs, camera / device / model status events, the monitoring ingestion API |
| Worker | Own records | Super / Factory Admin | Own profile, workstation, attendance and notifications |

A user's reach is the widest of their roles. A non-platform user with no
active factory keeps only self-service permissions.

## 3. Role → permission matrix

✓ = granted. *R* = reserved (defined now, enforced when its module ships).
*P* = platform only.

| Permission | Super | F.Admin | Manager | Supervisor | Monitor | HR | IT/Dev | Worker |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| **Platform** |
| `factories.view` | ✓ | ✓ | ✓ | | | | | |
| `factories.manage` *P* | ✓ | | | | | | | |
| `platform.settings.manage` *P* | ✓ | | | | | | | |
| `knowledge.manage` *P* | ✓ | | | | | | | |
| **Users & access** |
| `users.view` | ✓ | ✓ | | | | | | |
| `users.create` | ✓ | ✓ | | | | | | |
| `users.update` | ✓ | ✓ | | | | | | |
| `users.deactivate` | ✓ | ✓ | | | | | | |
| `users.reset_password` | ✓ | ✓ | | | | | | |
| `roles.view` | ✓ | ✓ | | | | | | |
| `roles.assign` | ✓ | ✓ | | | | | | |
| `audit.view` | ✓ | ✓ | | | | | | |
| **Organization** |
| `lines.view` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | |
| `lines.manage` | ✓ | ✓ | | | | | | |
| `departments.manage` | ✓ | ✓ | | | | | | |
| `settings.manage` | ✓ | ✓ | | | | | | |
| **Workforce** |
| `workers.view` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | | |
| `workers.details.view` | ✓ | ✓ | ✓ | | | ✓ | | |
| `workers.manage` | ✓ | ✓ | | | | ✓ | | |
| `workers.assign` | ✓ | ✓ | ✓ | ✓ | | | | |
| **Attendance & shifts** |
| `attendance.view` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | | |
| `attendance.record` | ✓ | ✓ | | ✓ | | ✓ | | |
| `attendance.manage` | ✓ | ✓ | | | | ✓ | | |
| `attendance.approve` | ✓ | ✓ | ✓ | | | ✓ | | |
| `shifts.manage` | ✓ | ✓ | | | | ✓ | | |
| `shifts.assign` | ✓ | ✓ | | ✓ | | ✓ | | |
| `leave.manage` *R* | ✓ | ✓ | | | | ✓ | | |
| `overtime.view` | ✓ | ✓ | ✓ | ✓ | | ✓ | | |
| `overtime.manage` | ✓ | ✓ | ✓ | ✓ | | ✓ | | |
| `overtime.approve` | ✓ | ✓ | ✓ | ✓ | | ✓ | | |
| **Monitoring** |
| `monitoring.view` | ✓ | ✓ | ✓ | ✓ (their lines) | ✓ | | | |
| `monitoring.alerts.review` | ✓ | ✓ | ✓ | ✓ (their lines) | ✓ | | | |
| `monitoring.alerts.escalate` | ✓ | ✓ | ✓ | ✓ (their lines) | ✓ | | | |
| `monitoring.alerts.dismiss` | ✓ | ✓ | ✓ | | | | | |
| `monitoring.events.review` | ✓ | ✓ | ✓ | ✓ (their lines) | ✓ | | | |
| `monitoring.events.ingest` | ✓ | ✓ | | | | | ✓ | |
| `monitoring.simulate` | ✓ | ✓ | | | ✓ | | | |
| `cameras.view` | ✓ | ✓ | ✓ | ✓ (their lines) | ✓ | | ✓ | |
| `cameras.manage` | ✓ | ✓ | | | | | ✓ | |
| **Production** |
| `production.view` | ✓ | ✓ | ✓ | ✓ | | | | |
| `production.manage` | ✓ | ✓ | ✓ | | | | | |
| `production.setup` | ✓ | ✓ | | | | | | |
| `production.alerts.review` | ✓ | ✓ | | ✓ | | | | |
| `qc.view` | ✓ | ✓ | ✓ | ✓ | | | | |
| `qc.manage` | ✓ | ✓ | | ✓ | | | | |
| `merchandising.view` | ✓ | ✓ | ✓ | ✓ | | | | |
| `merchandising.manage` | ✓ | ✓ | ✓ | | | | | |
| `approvals.view` | ✓ | ✓ | ✓ | ✓ | | | | |
| `approvals.decide` | ✓ | ✓ | ✓ | | | | | |
| `approvals.decide_high` | ✓ | ✓ | | | | | | |
| **Devices** |
| `devices.view` | ✓ | ✓ | ✓ | | ✓ | | ✓ | |
| `devices.manage` | ✓ | ✓ | | | | | ✓ | |
| `devices.logs.view` | ✓ | ✓ | | | | | ✓ | |
| `devices.simulate` | ✓ | ✓ | | | | | ✓ | |
| **Reports & AI** |
| `reports.view` | ✓ | ✓ | ✓ | | | ✓ | | |
| `knowledge.view` | ✓ | ✓ | ✓ | ✓ | | | | |
| `agents.run` | ✓ | ✓ | ✓ | | | | | |
| **Self-service** |
| `self.view` | ✓ | ✓ | | | | | | ✓ |
| `notifications.view` *R* | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |

`GET /api/permissions` and the **Permissions** page show the live matrix.

## 4. Rules the server enforces

1. **Every endpoint is guarded.** Anonymous: login, refresh, logout, register,
   forgot/reset password, options, `/health`. Signed-in only: `/api/auth/me`,
   `/api/account/*`, `/`. Everything else needs a permission
   (`EndpointMatrix` in the tests lists all of them).
2. **Factory isolation.** Non-platform callers see and change only users,
   workers, lines, factories and audit entries of their own factories. Records
   outside the scope answer **404** (their existence isn't revealed).
3. **Line isolation.** A Production Supervisor sees only workers on assigned
   lines, can move workers only between those lines, and can record QC only for
   them.
4. **Self access.** A Worker reads only their own linked worker record
   (`GET /api/workers/me`, or its id / Worker ID).
5. **Worker detail tiers.** Everyone who can see a worker gets the directory
   fields (Worker ID, name, photo, placement, designation, status).
   `workers.details.view` adds employment details (employee number, joining
   date, grade, type, status history) and the CSV export without personal
   columns. `workers.manage` and the worker themself also get personal
   details (phone, email, gender, date of birth, address, emergency contact).
   The IT / Device Administrator has no access to worker records.
6. **Worker lifecycle.** Status changes follow a fixed transition table
   (`WorkerStatusTransitions`), are checked under a row lock, need a reason to
   suspend, separate or re-hire, and are kept in the status history. Resigning
   or terminating deactivates the worker's cards. Workers are never deleted;
   Worker IDs come from a database sequence, so concurrent registrations never
   share one and an ID is never handed out again.
7. **No escalation.** A caller grants only roles they manage *and* whose
   permissions they hold; manages a user only when they could grant every
   role that user holds; can't change their own roles, status or scope.
   Factory Admins can't touch Factory Admins or Super Admins.
8. **Super Admin protection.** Only Super Admins see or change Super Admin
   accounts; the last active Super Admin can't be deactivated or lose the role
   (checked under a row lock, so two admins can't remove each other at once).
9. **Sessions follow changes.** Access tokens carry roles, permissions,
   factory/line ids and a security stamp. Changing roles, scope, status, email
   or password rotates the stamp; the API rejects older tokens (401) and the
   browser silently refreshes to the new access. Deactivation and admin
   password resets also revoke the refresh token. (Per-instance cache: 30 s.)
10. **Temporary passwords.** Accounts created by an administrator, and admin
   resets, use a temporary password; until it is changed every permission
   check fails with `password_change_required`.
11. **Workstation assignments.** Reading needs `workers.view` (a worker reads
    their own with `self.view`); assigning, transferring and ending need
    `workers.assign` with the worker *and* the workstation in scope (a
    supervisor: their lines; never across factories). Only active workers,
    active workstations and active machines; a machine must stand at that
    workstation (or nowhere) and serve one assignment at a time. One active
    primary assignment per worker and at most `Capacity` workers per
    workstation, checked under row locks (worker → assignment → workstation)
    and backed by a partial unique index and an exclusion constraint
    (`EX_workstation_assignments_no_overlap`, `btree_gist`) that rejects
    overlapping periods. A transfer closes the old assignment and opens the
    new one at the same instant in one transaction. A worker who stops being
    Active has their assignment ended. Configuring workstations needs
    `lines.manage` (machines: or `devices.manage`); a workstation with
    assignments can't be deactivated, moved, or reduced below its occupancy.
12. **Shifts, attendance, breaks and overtime.** Shifts are per factory
    (times in the factory's time zone, overnight when the end is at or
    before the start, working days, grace periods, breaks); configuring them
    needs `shifts.manage`, and a shift with current or future assignments
    can't be deactivated. Assigning workers needs `shifts.assign` with the
    worker in scope (a supervisor: their lines); each worker is assigned in
    their own row-locked transaction, an overlapping earlier assignment is
    ended the day before, a later one is a conflict, and the exclusion
    constraint `EX_worker_shift_assignments_no_overlap` rejects overlapping
    dates. Punches (`attendance.record`), simulator taps (`attendance.manage`)
    and reader events (`devices.manage`, worker found by an active card UID)
    all go through one pipeline: the worker row is locked, an idempotency key
    already seen returns the first result, a repeat inside the duplicate
    window is stored as *Duplicate*, and refused events are stored as
    *Rejected* with a code. `attendance_events` is append-only (database
    triggers), and simulator events must carry `IsSimulated` (check
    constraint). One open session and one active break per worker (partial
    unique indexes). A session that should have ended is flagged
    *MissingCheckOut* by the 5-minute review sweep or by the worker's next
    punch; no time is invented. Corrections need `attendance.manage` to
    request and `attendance.approve` to decide, by someone other than the
    requester; originals are kept. Overtime: `overtime.manage` requests and
    cancels, `overtime.approve` approves or rejects (not your own), approved
    minutes are at most the requested minutes, actual minutes come from
    attendance inside the approved window (breaks excluded), and payable
    minutes are confirmed by `attendance.manage` (HR), up to the lower of
    approved and actual; nothing feeds salary automatically. Policy limits
    (daily / weekly overtime) only warn.
13. **Cameras and devices.** Cameras are read with `cameras.view` (a
    Production Supervisor: cameras whose main line or active coverage is one
    of their lines; anything else answers 404) and changed with
    `cameras.manage` plus factory-wide reach. Floor, department, line and
    covered workstations must belong to the camera's factory, and a covered
    workstation must be on the camera's floor. A zone is a 3–64 point simple
    polygon with every coordinate in 0–1, drawn over the camera's reference
    frame; a zone that names a workstation or line must be inside the
    camera's coverage, and coverage used by an active zone can't be removed.
    Zone edits bump a version (`expectedVersion` refuses stale edits) and
    write an append-only revision. Video sources are versioned (one current
    per camera); RTSP / HTTP URLs are validated (scheme; no loopback,
    link-local / metadata, multicast or unspecified hosts; no secrets in the
    query) and stored without credentials; user name and password are
    AES-256-GCM encrypted with `AI_SETTINGS_ENCRYPTION_KEY`, bound to the
    source row, and never returned, logged or audited. The server never
    connects to a camera URL. Uploaded videos (MP4 / WebM) and frames (JPEG /
    PNG / WebP) are identified by their bytes. A camera can be deleted only
    without zones and without device history. Devices are read with
    `devices.view`, their history with `devices.logs.view` (or
    `devices.manage`), and registered / changed with `devices.manage`.
    Every device message (real hardware with `devices.manage`, the simulator
    with `devices.simulate`) goes through one pipeline under a row lock: a
    repeated `messageId` is recorded once, messages more than 5 minutes
    ahead or 7 days old are refused, a disabled device refuses everything,
    and old buffered messages never move the state backwards. A simulated
    device accepts only simulator messages and a real one only device
    messages; check constraints enforce the same on every heartbeat and
    event, and a simulated device is never reported as physically connected.
    A device with no message inside its offline window counts as offline at
    once, and the health monitor (every minute, or
    `POST /api/devices/health/check`) records a *HeartbeatMissed* event.
    `camera_zone_revisions`, `device_heartbeats` and `device_events` are
    append-only (database triggers); a device with history can't be deleted.
14. **Central monitoring.** One event pipeline for every producer: the
    ingestion API (`monitoring.events.ingest`, REAL events only), the
    monitoring simulator (`monitoring.simulate`, SIMULATED only), the device
    pipeline (connection, error and status changes of real or simulated
    devices; a camera's health device also reports the camera) and camera
    status changes. It validates the contract per event type (see
    `MONITORING_EVENTS.md`) and every reference against the event's factory,
    stores one event per factory and `idempotencyKey` (a resend returns the
    stored event as *Duplicate*), and applies the alert rules in the same
    transaction. A `trackId` is a temporary camera-local number, never a
    worker. A `workerId` is accepted only with `identity.method = RFID_BADGE`
    and a card UID that is an active card of that worker in that factory;
    visual identification is refused, an UNCERTAIN_DETECTION never names
    anyone, and simulated events never do (check constraints enforce it too).
    **Reach:** Super Admin every factory; factory roles their factories;
    Production Supervisors only events and alerts on their lines. VISION
    events (people, tracks, zones, activity) need `monitoring.view`; with only
    `devices.view` (IT / Device Administrator) a user sees SYSTEM events
    (camera, device, model status) and their alerts. **Alerts:** raised for
    cameras / devices / models going offline or failing, uncertain detections
    (review) and restricted-zone entries, never for productivity; a repeat
    joins the open alert, a recovery is noted but a person still resolves it.
    OPEN → ACKNOWLEDGED → ESCALATED → RESOLVED (resolving needs an
    acknowledgement, a resolution code and a note), DISMISSED from any active
    state with a reason (`monitoring.alerts.dismiss`); closed alerts never
    change, a new occurrence raises a new alert. An optional
    `expectedVersion` refuses an action on an alert someone else just
    changed. Every action is written to the alert's history and the audit
    log. Alerts never change pay, attendance or discipline. **Reviews** of an
    event are stored beside it (`monitoring.events.review`).
    `monitoring_events`, `monitoring_event_reviews` and
    `monitoring_alert_actions` are append-only (database triggers).
    **Real time:** `/hubs/monitoring` needs `monitoring.view` or
    `devices.view` to connect; `Subscribe(factoryId?)` joins only the groups
    the token allows (a factory's VISION / SYSTEM group, or a supervisor's
    line groups); messages are sent to those groups only, never to everyone.
    The connection closes when the token expires and the client reconnects
    with a fresh one.
15. **Audit.** Sign-ins and every user, role, scope, status, password, factory,
   line, department, floor, worker, card, import, export, shift, shift
   assignment, manual punch, correction, overtime decision, attendance policy,
   reader, camera (registration, edit, status, coverage, source, reference
   frame, deletion), zone, device change, monitoring alert action, event
   review and simulation start / stop are recorded with
   actor, before/after and reason. Passwords are never logged, and worker edits
   record which fields changed, not their personal values.

Errors are RFC 7807 problem details with a stable `code`, e.g.
`permission_denied` (+ `requiredPermissions`), `password_change_required`,
`role_not_grantable`, `factory_out_of_scope`, `line_out_of_scope`,
`cannot_change_own_roles`, `insufficient_authority`, `last_super_admin`,
`account_deactivated`, `email_taken`.

## 5. API

| Endpoint | Permission |
|---|---|
| `GET /api/auth/me` | signed in — user, roles, permissions, scope, factories, lines |
| `GET/PUT /api/account/profile`, `POST /api/account/password` | signed in |
| `GET /api/users?search&role&factoryId&status&sort&page&pageSize` | `users.view` |
| `POST /api/users` | `users.create` (+ `roles.assign` to give roles) |
| `GET /api/users/{id}` | `users.view` |
| `PUT /api/users/{id}` | `users.update` |
| `POST /api/users/{id}/status` `{isActive, reason}` | `users.deactivate` |
| `PUT /api/users/{id}/roles` `{roles}` | `roles.assign` |
| `PUT /api/users/{id}/scope` `{factoryIds, lineIds}` | `users.update` |
| `POST /api/users/{id}/reset-password` `{temporaryPassword}` | `users.reset_password` |
| `GET /api/users/{id}/activity` | `audit.view` |
| `GET /api/roles`, `/api/roles/{id}`, `/api/permissions` | `roles.view` |
| `GET /api/factories`, `/api/factories/{id}` | `factories.view` or `users.view` |
| `POST /api/factories`, `PUT /api/factories/{id}`, `POST /api/factories/{id}/status` | `factories.manage` (Super Admin) |
| `GET /api/factories/{id}/lines` | `lines.view` or `users.update` |
| `GET /api/factories/unassigned-lines`, `PUT/DELETE /api/factories/{id}/lines/{lineId}` | `lines.manage` |
| `GET/POST /api/factories/{id}/departments`, `PUT …/departments/{departmentId}` | list: `workers.view`, `lines.view` or `departments.manage`; write: `departments.manage` |
| `GET/POST /api/factories/{id}/floors`, `PUT …/floors/{floorId}` | list: `workers.view` or `lines.view`; write: `lines.manage` |
| `GET /api/workers?search&factoryId&departmentId&floorId&lineId&status&designation&employmentType&sort&page&pageSize` | `workers.view` |
| `GET /api/workers/{id}`, `GET /api/workers/{id}/profile`, `GET /api/workers/{id}/photo` (`{id}` = GUID or Worker ID) | `workers.view` (own record: `self.view`) |
| `GET /api/workers/me` | `self.view` |
| `GET /api/workers/statistics?factoryId` | `workers.view` |
| `GET /api/workers/export?…same filters…` | `workers.details.view` or `workers.manage` |
| `POST /api/workers`, `PUT /api/workers/{id}` | `workers.manage` |
| `PATCH /api/workers/{id}/status` `{status, reason, effectiveOn}` | `workers.manage` |
| `PUT/DELETE /api/workers/{id}/photo` | `workers.manage` |
| `GET/POST /api/workers/{id}/cards`, `PATCH /api/workers/{id}/cards/{cardId}` `{reason}` | `workers.manage` |
| `GET /api/workers/import/template`, `POST /api/workers/import` (multipart `file`, `factoryId`, `dryRun`) | `workers.manage` |
| `PUT /api/workers/{id}/workstation` `{lineId}` (home line only; a workstation label is refused, use assignments) | `workers.assign` or `workers.manage` |
| `GET /api/workstations?search&factoryId&departmentId&floorId&lineId&occupancy&includeInactive&page&pageSize`, `GET /api/workstations/{id}` | `workers.view`, `lines.view` or `workers.assign` (assigned workers only with `workers.view`) |
| `POST /api/workstations`, `POST /api/workstations/bulk`, `PUT /api/workstations/{id}` | `lines.manage` |
| `GET /api/machines?factoryId&workstationId&unplaced&search` | `lines.view`, `devices.view` or `workers.assign` |
| `POST /api/machines`, `PUT /api/machines/{id}` | `lines.manage` or `devices.manage` |
| `GET /api/assignments?…filters…`, `GET /api/assignments/active`, `GET /api/assignments/statistics` | `workers.view` |
| `GET /api/assignments/{id}`, `GET /api/workers/{id}/assignments` (`{id}` may be `me`) | `workers.view` (own: `self.view`) |
| `GET /api/workstations/{id}/assignments` | `workers.view` |
| `POST /api/assignments` `{workerId, workstationId, machineId?, startAt?, reason?, notes?}` | `workers.assign` |
| `POST /api/assignments/{id}/transfer` `{workstationId, reason, machineId?, effectiveAt?, notes?}` | `workers.assign` |
| `POST /api/assignments/{id}/end` `{reason, endAt?}` | `workers.assign` |
| `GET /api/shifts?factoryId`, `GET /api/shifts/{id}` | `attendance.view`, `shifts.manage` or `shifts.assign` |
| `POST /api/shifts`, `PUT /api/shifts/{id}` (edit, activate, deactivate) | `shifts.manage` |
| `GET /api/shift-assignments?…filters…` | `attendance.view` or `shifts.assign` |
| `POST /api/shift-assignments` `{shiftId, effectiveFrom, effectiveTo?, workerIds? or lineId? or departmentId?, reason?}` | `shifts.assign` |
| `POST /api/shift-assignments/{id}/end` `{effectiveTo, reason}` | `shifts.assign` |
| `GET /api/workers/{id}/shift-assignments` (`{id}` may be `me`) | `attendance.view` (own: `self.view`) |
| `POST /api/attendance/check-in`, `/check-out` `{workerId, occurredAt?, idempotencyKey?, note?}` | `attendance.record` |
| `POST /api/breaks/start`, `/end` `{workerId, breakType?, occurredAt?, idempotencyKey?}` | `attendance.record` |
| `POST /api/attendance/events` `{readerId, cardUid, eventType, occurredAt, idempotencyKey}` (real readers only) | `devices.manage` |
| `GET /api/attendance?…filters, sort, page…`, `/statistics`, `/events`, `/report`, `/report/export`; `GET /api/breaks` | `attendance.view` |
| `GET /api/attendance/{workerId}` (`me` allowed), `GET /api/attendance/sessions/{id}` | `attendance.view` (own: `self.view`) |
| `POST /api/attendance/{sessionId}/correction`, `POST /api/attendance/corrections`, `POST /api/attendance/review/run` | `attendance.manage` |
| `GET /api/attendance/corrections` | `attendance.manage` or `attendance.approve` |
| `POST /api/attendance/corrections/{id}/approve`, `/reject` | `attendance.approve` (not the requester) |
| `GET /api/attendance/policy?factoryId` / `PUT` | `attendance.view` or `shifts.manage` / `attendance.manage` |
| `GET /api/attendance/readers` / `POST`, `PUT /api/attendance/readers/{id}` | `attendance.manage` or `devices.view` / `devices.manage` |
| `GET /api/overtime?…filters…` | `overtime.view` or `overtime.approve` |
| `GET /api/overtime/{id}` | `overtime.view` or `overtime.approve` (own: `self.view`) |
| `POST /api/overtime/request`, `/{id}/submit`, `/{id}/cancel` | `overtime.manage` |
| `POST /api/overtime/{id}/approve` `{approvedMinutes?, note?}`, `/{id}/reject` `{reason}` | `overtime.approve` (not the requester) |
| `POST /api/overtime/{id}/confirm-payable` `{payableMinutes, note?}` | `attendance.manage` |
| `GET /api/simulator/rfid/readers`, `/events`; `POST /api/simulator/rfid/check-in`, `/check-out`, `/break-start`, `/break-end` | `attendance.manage` |
| `GET /api/cameras?search&factoryId&floorId&lineId&workstationId&status&type&source&health&sort&page&pageSize`, `GET /api/cameras/overview?factoryId`, `GET /api/cameras/{id}` | `cameras.view` (supervisors: their lines) |
| `POST /api/cameras` `{factoryId, name, cameraType, floorId, code?, departmentId?, lineId?, location?, coverageLineIds?, workstationIds?, healthDevice?}`, `PUT /api/cameras/{id}`, `PATCH /api/cameras/{id}/status` `{status, reason}`, `DELETE /api/cameras/{id}` | `cameras.manage` |
| `PUT /api/cameras/{id}/coverage` `{lineIds, workstationIds}` | `cameras.manage` |
| `GET /api/cameras/{id}/sources` (versions, no credentials), `GET /api/cameras/{id}/source/video`, `GET /api/cameras/{id}/reference-frame` | `cameras.view` |
| `PUT /api/cameras/{id}/source` `{sourceType, streamUrl?, userName?, password?, keepCredentials?, webcamLabel?, notes?}`, `PUT /api/cameras/{id}/source/video` (multipart `video`), `PUT /api/cameras/{id}/reference-frame` (multipart `image`, `source`, `frameSeconds`) | `cameras.manage` |
| `GET /api/cameras/{id}/zones?includeInactive`, `GET /api/camera-zones/{id}/history` | `cameras.view` |
| `POST /api/cameras/{id}/zones` `{name, zoneType, points:[{x,y}], workstationId?, lineId?, color?, notes?}`, `PUT /api/camera-zones/{id}` (+ `isActive`, `expectedVersion`) | `cameras.manage` |
| `GET /api/devices?search&factoryId&category&status&health&simulated&lineId&cameraId&sort&page&pageSize`, `GET /api/devices/{id}`, `GET /api/devices/health?factoryId` | `devices.view` |
| `POST /api/devices`, `PUT /api/devices/{id}`, `PATCH /api/devices/{id}/status`, `DELETE /api/devices/{id}`, `POST /api/devices/health/check` | `devices.manage` |
| `POST /api/devices/{id}/heartbeat` `{occurredAt?, messageId?, firmwareVersion?, metrics?}`, `POST /api/devices/{id}/events` `{kind, code?, message?, severity?}`, `POST /api/devices/ingest` `{factoryId, deviceCode, kind, …}` (real devices only) | `devices.manage` |
| `GET /api/devices/events?…filters…`, `GET /api/devices/{id}/events`, `GET /api/devices/{id}/heartbeats` | `devices.logs.view` or `devices.manage` |
| `GET /api/monitoring/overview?factoryId&departmentId&floorId&lineId&cameraId&eventType&alertStatus&from&to`, `GET /api/monitoring/events?…filters…&page&pageSize`, `GET /api/monitoring/events/{id}`, `GET /api/monitoring/alerts?…filters…&status (or ACTIVE)&sort`, `GET /api/monitoring/alerts/{id}` | `monitoring.view` or `devices.view` (SYSTEM only) |
| `GET /api/monitoring/cameras?factoryId&floorId&lineId&connection&search`, `GET /api/monitoring/cameras/{id}` (no stream URLs or credentials) | `monitoring.view` or `cameras.view` |
| `GET /api/monitoring/contract` | `monitoring.view`, `devices.view` or `monitoring.events.ingest` |
| `POST /api/monitoring/events` (REAL events; 201, or 200 *Duplicate*; see `MONITORING_EVENTS.md`) | `monitoring.events.ingest` |
| `POST /api/monitoring/events/{id}/reviews` `{decision, note?}` | `monitoring.events.review` |
| `POST /api/monitoring/alerts/{id}/acknowledge` `{note?, expectedVersion?}`, `/resolve` `{resolutionCode, note, expectedVersion?}` | `monitoring.alerts.review` |
| `POST /api/monitoring/alerts/{id}/escalate` `{toRole, reason, expectedVersion?}` | `monitoring.alerts.escalate` |
| `POST /api/monitoring/alerts/{id}/dismiss` `{reason, expectedVersion?}` | `monitoring.alerts.dismiss` |
| `GET /api/monitoring/simulator?factoryId`, `POST /api/monitoring/simulator/start` `{factoryId, cameraIds?, eventsPerMinute?, durationMinutes?, eventTypes?}`, `POST /api/monitoring/simulator/{runId}/stop` `{reason?}`, `POST /api/monitoring/simulator/events` `{factoryId, eventType, cameraId?, zoneId?, deviceId?, trackId?, confidence?, activity?, toStatus?}` | `monitoring.simulate` |
| `GET /api/integrations/cv/cameras`, `/cameras/{id}`, `/cameras/{id}/video` (camera sources with decrypted stream credentials, active zones, recorded video) | the computer-vision **service key** only (`X-Service-Key`); 403 for every person |
| SignalR `/hubs/monitoring` (methods `Subscribe(factoryId?)`, `Unsubscribe()`; messages `monitoringEvent`, `monitoringAlert`, `cameraStatus`, `deviceStatus`) | `monitoring.view` or `devices.view` |
| `GET /api/simulator/devices`, `/events`; `POST /api/simulator/devices/online`, `/offline`, `/heartbeat` `{deviceId, count?, intervalSeconds?}`, `/error` (simulated devices only) | `devices.simulate` |
| `PUT /api/workers/{id}/account` `{userId}` (link / unlink) | `users.update` |
| `POST /api/workers/{id}/account` `{email, temporaryPassword}` (create a Worker login) | `users.create` |
| `GET /api/audit?entityType&entityId&action&actorUserId&factoryId&limit&before` | `audit.view` (scoped) |

The existing modules (production, QC, merchandising, approvals, sensors,
knowledge, settings, agents) are guarded by their module permissions; see
`EndpointAccessMatrixTests.cs` for the per-endpoint list.

## 6. Frontend

| Page | Route | Shown to |
|---|---|---|
| Login (+ deactivated message, temp-password redirect) | `/login` | everyone |
| Users (search, filters, sort, pages) | `/app/admin/users` | `users.view` |
| New user | `/app/admin/users/new` | `users.create` |
| User details: profile, roles & scope, activity, security | `/app/admin/users/{id}` | `users.view` (actions follow `can` from the API) |
| Roles | `/app/admin/roles` | `roles.view` |
| Permissions matrix | `/app/admin/permissions` | `roles.view` |
| Factories, lines, departments & floors | `/app/admin/factories` | `factories.manage`, `lines.manage` or `departments.manage` |
| Worker directory (search, filters, sort, pages, export, summary) | `/app/workers` | `workers.view` |
| Add worker | `/app/workers/new` | `workers.manage` |
| Worker details (profile, status, cards, login, photo) | `/app/workers/{id}` | `workers.view` (actions follow `can` from the API) |
| Edit worker | `/app/workers/{id}/edit` | `workers.manage` |
| Import workers (CSV, preview, confirm) | `/app/workers/import` | `workers.manage` |
| Worker statistics | `/app/workers/statistics` | `workers.view` |
| Assignment dashboard (occupancy, line layout, recent transfers) | `/app/assignments` | `workers.view` |
| Workstations (overview, add / add a series) | `/app/assignments/workstations` | `workers.view`, `lines.view` or `workers.assign` |
| Workstation details (assigned workers, assign / transfer / end, machines, configure, history) | `/app/assignments/workstations/{id}` | as above; actions follow permissions |
| Assignment history | `/app/assignments/history` | `workers.view` |
| Worker profile → Workstation assignment (current, assign / transfer / end, timeline) | `/app/workers/{id}` | `workers.view` |
| Attendance dashboard (present, absent, late, checked in, missing punches, breaks, OT, corrections, events by source) | `/app/attendance` | `attendance.view` |
| Attendance records (search, filters, sort, pages, export, request correction) | `/app/attendance/records` | `attendance.view` |
| Worker attendance details (shift, sessions, breaks, overtime, corrections, shift history) | `/app/attendance/workers/{id}` | `attendance.view` (own: `self.view`) |
| Break management (running, overdue, history) | `/app/attendance/breaks` | `attendance.view` |
| Attendance corrections (approve / reject) | `/app/attendance/corrections` | `attendance.manage` or `attendance.approve` |
| Shift management and attendance policy | `/app/attendance/shifts` | `attendance.view`, `shifts.manage` or `shifts.assign` |
| Worker shift assignment (one, several, a line, a department; end) | `/app/attendance/shift-assignments` | `attendance.view` or `shifts.assign` |
| Overtime requests | `/app/attendance/overtime` | `overtime.view` or `overtime.approve` |
| Overtime approval dashboard (pending, running, payable to confirm) | `/app/attendance/overtime/approvals` | `overtime.approve` |
| RFID attendance simulator (SIMULATED) | `/app/attendance/simulator` | `attendance.manage` |
| Attendance reports (per-worker totals, CSV) | `/app/attendance/reports` | `attendance.view` |
| Camera & device overview (camera configuration, device health, needs attention, recent events) | `/app/cameras-devices` | `cameras.view` or `devices.view` |
| Camera directory (search, filters, sort, pages) | `/app/cameras-devices/cameras` | `cameras.view` |
| Register / edit camera (placement, coverage, health device) | `/app/cameras-devices/cameras/new`, `/{id}/edit` | `cameras.manage` |
| Camera details (metadata, coverage, video source, recorded video / webcam preview, reference frame, health, zones, AI status) | `/app/cameras-devices/cameras/{id}` | `cameras.view` (actions: `cameras.manage`) |
| Camera zone configuration (polygon editor, versions, history) | `/app/cameras-devices/cameras/{id}/zones` | `cameras.view` (editing: `cameras.manage`) |
| Device directory | `/app/cameras-devices/devices` | `devices.view` |
| Register / edit device | `/app/cameras-devices/devices/new`, `/{id}/edit` | `devices.manage` |
| Device details (health, events, heartbeats, status, ingestion contract) | `/app/cameras-devices/devices/{id}` | `devices.view` (history: `devices.logs.view`) |
| Device health dashboard and event log | `/app/cameras-devices/health` | `devices.view` |
| Device simulator (SIMULATED) | `/app/cameras-devices/simulator` | `devices.simulate` |
| Central monitoring room (cameras, devices, alerts, live event feed, line and factory status; filters) | `/app/monitoring` | `monitoring.view` or `devices.view` |
| Camera monitoring grid | `/app/monitoring/cameras` | `monitoring.view` or `cameras.view` |
| Camera expanded view (recorded playback, local webcam preview, stream status, zones, AI status, alerts, live events) | `/app/monitoring/cameras/{id}` | `monitoring.view` or `cameras.view` |
| Event timeline and event details (reviews, related alerts) | `/app/monitoring/events` | `monitoring.view` or `devices.view` (reviewing: `monitoring.events.review`) |
| Alert management | `/app/monitoring/alerts` | `monitoring.view` or `devices.view` (actions: their permissions) |
| Alert details (history, evidence, events) | `/app/monitoring/alerts/{id}` | `monitoring.view` or `devices.view` |
| Device health overview (live) | `/app/monitoring/devices` | `devices.view` |
| Monitoring simulator (SIMULATED) | `/app/monitoring/simulator` | `monitoring.simulate` |
| Account settings (profile, password, my access) | `/app/account` | everyone |
| My profile (worker self-service) | `/app/me` | `self.view` |
| Access denied | shown in place, or `/app/access-denied` | — |

Navigation items declare their permissions in `src/lib/navigation.ts`; the app
layout shows **Access denied** for a page the user can't open, sends a pending
temporary password to `/app/account`, and starts users without an overview
(e.g. Workers) on their own page.

## 7. Getting started

1. Set `SEED_ADMIN_EMAIL` / `SEED_ADMIN_PASSWORD` and start the API: the first
   Super Admin is created.
2. Sign in → **Factories & lines** → *New factory*; attach its lines.
3. **Users** → *New user* → role *Factory Admin*, that factory. Share the
   temporary password; they replace it at first sign-in.
4. The Factory Admin creates the other users (HR, supervisors with lines, …).
5. The Factory Admin adds the factory's departments (and floors) on
   **Factories, lines & departments**.
6. HR registers workers (**Workers** → *Add worker* or *Import CSV*); a Factory
   Admin gives a worker a login only if they should see their own records.

## 8. Known limitations

- The production, QC, merchandising, approvals, sensors and knowledge modules
  hold single-plant data without a factory column. They are permission-gated,
  and Production Supervisors are line-limited on QC writes, but their records
  are not yet partitioned by factory.
- Leave and notifications are reserved permissions: the
  roles carry them, but those modules aren't built yet. Attendance doesn't yet account for leave or holidays: a scheduled
  worker with no attendance counts as absent.
- Attendance: a reader event with an unknown card UID is refused (404
  `card_not_recognised`) and not stored, since there is no worker to attach
  it to. Overtime limits from the attendance policy only warn. Payable
  overtime is a confirmed number for payroll; no salary is calculated.
- Factory shifts (`shifts`) are separate from production planning's
  plant-wide `production_shifts`; a factory shift can name a production
  shift code to link the two.
- Cameras and devices: no computer vision runs (no detection, tracking or
  AI results), and the server never connects to camera streams, so a
  configured RTSP / HTTP source is *not checked*. Storing camera credentials
  needs `AI_SETTINGS_ENCRYPTION_KEY` on the API server. Device hardware
  authenticates as a user account with `devices.manage` (no per-device
  tokens yet), and there is no MQTT bridge yet: the HTTP endpoints are the
  contract it would call. Heartbeats and device events are kept without a
  retention policy. Reference frames and recorded videos live on the local
  file store (`CAMERA_FILE_STORAGE_PATH`, limits `CAMERA_IMAGE_MAX_MB` /
  `CAMERA_VIDEO_MAX_MB`, default 8 / 50 MB); the browser downloads a video
  whole to play it.
- Central monitoring: vision events come from the computer-vision service
  (REAL: YOLO person detection + ByteTrack, see `computer-vision/README.md`),
  from the monitoring simulator (SIMULATED) or from producers calling the
  ingestion API. The computer-vision service signs in with its own service
  key (`CV_SERVICE_KEY`, factories in `CV_SERVICE_FACTORIES`); other producers
  still use a user account with `monitoring.events.ingest`. One service key
  exists (no rotation without a restart, no per-instance keys yet). Live camera
  pictures aren't shown: RTSP / HTTP streams are not opened by the server,
  recorded videos play back, and a "local webcam" preview uses the viewer's
  own browser camera. HR has no central monitoring access (attendance events
  stay in the attendance module). The hub checks the token when connecting
  and closes at token expiry; a role or scope change applies to a live
  connection at its next reconnect (at most one token lifetime). Events and
  alert history are kept without a retention policy. A simulation run acts
  with its starter's identity limited to the run's factory and ends by
  itself (at most 2 hours, 5,000 events).
- System roles are fixed; custom roles are not supported (by design for now).
- Token freshness is cached for 30 s per API instance: on a multi-instance
  deployment another instance may accept an old token for up to 30 s.
