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
| Monitoring Operator | Assigned factories | Super / Factory Admin | Monitoring dashboard, cameras, acknowledge and escalate alerts |
| HR / Attendance Officer | Assigned factories | Super / Factory Admin | Worker records, attendance, shifts, leave, overtime |
| IT / Device Administrator | Assigned factories | Super / Factory Admin | Devices, integrations, technical logs |
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
| **Attendance** (all *R*) |
| `attendance.view` | ✓ | ✓ | ✓ | ✓ | | ✓ | | |
| `attendance.manage` | ✓ | ✓ | | | | ✓ | | |
| `shifts.manage` | ✓ | ✓ | | | | ✓ | | |
| `leave.manage` | ✓ | ✓ | | | | ✓ | | |
| `overtime.view` | ✓ | ✓ | ✓ | | | ✓ | | |
| `overtime.manage` | ✓ | ✓ | | | | ✓ | | |
| **Monitoring** |
| `monitoring.view` | ✓ | ✓ | ✓ | ✓ | ✓ | | | |
| `monitoring.alerts.review` | ✓ | ✓ | | | ✓ | | | |
| `monitoring.alerts.escalate` *R* | ✓ | ✓ | | | ✓ | | | |
| `cameras.view` *R* | ✓ | ✓ | | | ✓ | | ✓ | |
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
| `devices.view` | ✓ | ✓ | ✓ | | | | ✓ | |
| `devices.manage` | ✓ | ✓ | | | | | ✓ | |
| `devices.logs.view` *R* | ✓ | ✓ | | | | | ✓ | |
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
12. **Audit.** Sign-ins and every user, role, scope, status, password, factory,
   line, department, floor, worker, card, import and export are recorded with
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
- Attendance, shifts, leave, overtime, cameras, device logs and
  notifications are reserved permissions: the roles carry them, but those
  modules aren't built yet.
- System roles are fixed; custom roles are not supported (by design for now).
- Token freshness is cached for 30 s per API instance: on a multi-instance
  deployment another instance may accept an old token for up to 30 s.
