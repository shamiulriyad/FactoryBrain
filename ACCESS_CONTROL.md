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
| Worker profile | `workers` | **Separate from login accounts.** A worker needs no login; at most one account can be linked (`UserId`) so the worker sees their own records. |
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
| `departments.manage` *R* | ✓ | ✓ | | | | | | |
| `settings.manage` | ✓ | ✓ | | | | | | |
| **Workforce** |
| `workers.view` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | | |
| `workers.manage` | ✓ | ✓ | | | | ✓ | | |
| `workers.assign` | ✓ | ✓ | | ✓ | | | | |
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
4. **Self access.** A Worker reads only their own linked worker profile
   (`GET /api/workers/me`). Employment details (phone, grade, joining date) are
   returned only to `workers.manage` holders and the worker themself.
5. **No escalation.** A caller grants only roles they manage *and* whose
   permissions they hold; manages a user only when they could grant every
   role that user holds; can't change their own roles, status or scope.
   Factory Admins can't touch Factory Admins or Super Admins.
6. **Super Admin protection.** Only Super Admins see or change Super Admin
   accounts; the last active Super Admin can't be deactivated or lose the role
   (checked under a row lock, so two admins can't remove each other at once).
7. **Sessions follow changes.** Access tokens carry roles, permissions,
   factory/line ids and a security stamp. Changing roles, scope, status, email
   or password rotates the stamp; the API rejects older tokens (401) and the
   browser silently refreshes to the new access. Deactivation and admin
   password resets also revoke the refresh token. (Per-instance cache: 30 s.)
8. **Temporary passwords.** Accounts created by an administrator, and admin
   resets, use a temporary password; until it is changed every permission
   check fails with `password_change_required`.
9. **Audit.** Sign-ins and every user, role, scope, status, password, factory,
   line and worker change are recorded with actor, before/after and reason.
   Passwords are never logged.

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
| `GET /api/workers`, `GET /api/workers/{id}` | `workers.view` (own profile: `self.view`) |
| `GET /api/workers/me` | `self.view` |
| `POST /api/workers`, `PUT /api/workers/{id}` | `workers.manage` |
| `PUT /api/workers/{id}/workstation` | `workers.assign` or `workers.manage` |
| `PUT /api/workers/{id}/account` `{userId}` | `users.update` |
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
| Factories & lines | `/app/admin/factories` | `factories.manage` or `lines.manage` |
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
5. HR creates worker profiles; a Factory Admin links a worker to a login if
   the worker should see their own records.

## 8. Known limitations

- The production, QC, merchandising, approvals, sensors and knowledge modules
  hold single-plant data without a factory column. They are permission-gated,
  and Production Supervisors are line-limited on QC writes, but their records
  are not yet partitioned by factory.
- Attendance, shifts, leave, overtime, cameras, device logs, departments and
  notifications are reserved permissions: the roles carry them, but those
  modules aren't built yet.
- System roles are fixed; custom roles are not supported (by design for now).
- Token freshness is cached for 30 s per API instance: on a multi-instance
  deployment another instance may accept an old token for up to 30 s.
