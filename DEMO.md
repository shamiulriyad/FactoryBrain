# Interactive demo

The interactive demo lets a reviewer sign in **once** with a shared demo credential, choose any of the
platform's roles, use that role's normal dashboard and pages, and switch roles without signing in again.

```
Homepage → "Explore demo" → /explore-demo (shared credential) → choose a role → that role's dashboard
        → "Demo · <role>" button in the top bar → choose another role (no new sign-in)
```

It runs **inside the normal deployment** (same website, same API) on its **own database**.
Each request is routed: a request that belongs to a demo session (demo token, demo sign-in route,
or a demo refresh cookie) uses `DEMO_BUNONBRAIN_DB`; every other request, and every production
background job, uses `BUNONBRAIN_DB` exactly as before. Real users keep signing in normally.
Startup refuses a demo database that is the production database or that has real accounts.

## 1. What the demo uses (audit of the existing project)

Nothing was invented for the demo. Roles, dashboards, permissions and data model are the ones in the code.

**Roles**: the 14 system roles in `AccessCatalog` (`BunonBrain.Application/Access/AccessCatalog.cs`).
The picker lists them from the API, so a role added to the catalog appears in the demo automatically.

| Role | Scope | Home dashboard (`components/dashboards/RoleHome.tsx`) | Main pages (sidebar, `lib/navigation.ts`) |
|---|---|---|---|
| Super Admin | every factory | platform admin | monitoring, planning, documents, analytics, approvals, workforce, AI, users / roles / factories, audit |
| Factory Admin | factory | factory admin | as above inside the factory, plus inventory, workers, attendance, assignments |
| Factory Manager | factory | manager | monitoring, planning, documents, orders, approvals, analytics, QC, inventory, workforce |
| Production Supervisor | assigned lines (Line 1, 2) | supervisor | alerts, floor board, planning, documents, QC, assignments, attendance, workers |
| Monitoring Operator | factory | operator | monitoring room, alerts, floor map, planning, documents, cameras & devices |
| HR / Attendance Officer | factory | HR | workforce, workers, attendance, assignments, documents, reports |
| IT / Device Administrator | factory | device | monitoring, cameras & devices, sensors, integrations, documents |
| Worker | own records | (redirects to My profile) | own profile, workstation, attendance |
| Document Controller, Merchandiser, Store Officer, QC Officer, Compliance Officer, Commercial Officer | factory | generic (pages the role can open) | documents plus their module (inventory, orders, QC / vision, workers) |

**Authentication** (unchanged): JWT access token (15 min, in memory) carrying roles, permissions,
factory and line scope and a security stamp; HttpOnly `fb_refresh` cookie scoped to `/api/auth`;
`SessionValidator` checks the stamp on every request; `[RequirePermission]` policies on every endpoint;
scope checks in the services through `AccessContext`. The demo reuses all of it.

The one thing that could not be reused as-is: a user row holds a single refresh token, so many
reviewers sharing one demo account would sign each other out. The demo therefore keeps its own
reviewer sessions (`demo_sessions`), each with its own refresh token.

## 2. How it works

| Piece | Where |
|---|---|
| Settings (`DEMO_*`), demo accounts (one per role, stable ids) | `BunonBrain.Application/Demo/DemoOptions.cs` |
| Reviewer sessions: sign-in, role switch, refresh, sign-out, revocation, audit | `BunonBrain.Infrastructure/Demo/DemoSessionService.cs` |
| Claiming the database, daily reset, background worker | `BunonBrain.Infrastructure/Demo/DemoDatabase.cs` |
| Baseline dataset (EF Core, repeatable) | `BunonBrain.Infrastructure/Demo/DemoSeeder.cs` |
| Routing a request to the demo or production database | `BunonBrain.Api/Middleware/DemoRoutingMiddleware.cs`, `DemoRoute` |
| Endpoints | `BunonBrain.Api/Controllers/DemoController.cs` |
| Guard for destructive operations | `BunonBrain.Api/Middleware/DemoGuardMiddleware.cs` |
| Tables `demo_environment`, `demo_sessions` | migration `AddDemoSessions` (used in the demo database; empty in production) |
| Sign-in and role picker | `frontend/src/app/explore-demo/page.tsx` |
| Homepage link, top-bar role switch | `app/(site)/page.tsx`, `components/layout/Topbar.tsx` |

**Sign-in and roles (all decided on the server)**

1. `POST /api/auth/demo/login` checks the shared credential (constant-time), creates a reviewer session
   with an absolute expiry (`DEMO_SESSION_HOURS`) and sets the refresh cookie. No role yet, no access token.
2. `POST /api/auth/demo/switch {roleId}`: the browser sends only a role id. The server checks it against the
   catalog, picks that role's demo account itself (never a user id from the browser), records the switch,
   rotates the cookie and returns the account's normal access token plus a `dsid` (demo session) claim.
3. Every request checks, besides the usual stamp: the session is live **and** still acting as this
   account. A token for the previous role stops working as soon as the reviewer switches.
4. `/api/auth/refresh` and `/api/auth/logout` work as before; with a demo session's cookie they act on that session.

Audit log entries: `demo.login`, `demo.role_selected`, `demo.session_revoked`, `demo.reset`,
`demo.baseline_seeded`, plus every normal audit entry the reviewer's actions produce.

**What reviewers can and cannot do**

Create, read and update work everywhere the chosen role is allowed to (workers, attendance, overtime
approvals, planning, documents, cameras, simulators …), shared with every other reviewer.
Refused with 403 for demo requests (production requests are not affected):

- deleting anything;
- password sign-in, sign-up, password reset with a demo token;
- changing the demo accounts (profile, password, status, roles, scope, worker link);
- deactivating a factory;
- platform settings: AI providers and keys, the public website, the knowledge index;
- paid AI calls, which would use production's provider key: the assistant, agent runs, camera AI,
  image inspection and AI document extraction.

Email is never sent for a demo request (the queue drops it). There are no payments or SMS in the product.

**Data**: one factory, *Bunon Demo Apparels Ltd.* (`BBD-1`), built through the application's own
services with the demo accounts as the acting users: 8 departments, 3 floors, 3 sewing lines with 30
workstations and machines, 46 workers with RFID cards, shift roster and workstation assignments, 6
working days of gate punches, overtime (approved, and some waiting for approval), 4 styles, 4 production
orders, line plans (prepared by the admin, activated by the manager), hourly output, capacity settings,
line targets, today's published daily plan, merchandising orders with milestones, store items, cameras,
RFID devices, and generated / submitted documents (daily production, attendance, leave applications).
While the demo is awake, today's punches and hourly output are added as the day goes on, and a
monitoring simulation (labelled SIMULATED) runs during working hours.

**Reset**: every day at `DEMO_RESET_TIME` (and at the first start, and whenever the dataset version
changes) every table except the marker is emptied in one transaction and the baseline is seeded again.
All reviewer sessions end. Production data and production sessions are never touched. On the free
Render plan the API sleeps, so the reset runs when the API next wakes; demo sign-in says "being
prepared" for the minute or two that takes.

## 3. Setting it up

1. Create a **new, empty** PostgreSQL database for the demo (for example a second Supabase project;
   use its session-pooler string with `;Maximum Pool Size=10`). Never the production database:
   startup refuses it ("points at the production database" / "not a demo database").
2. On the **existing** API service (Render: `factorybrain`) add:
   `DEMO_MODE=true`, `DEMO_BUNONBRAIN_DB` (the new database), `DEMO_LOGIN_EMAIL`, `DEMO_LOGIN_PASSWORD`
   (≥ 8 characters), and optionally `DEMO_ADMIN_KEY` (≥ 24 characters). Redeploy. Nothing changes on
   the website service: the homepage "Explore demo" link and `/explore-demo` are part of it.
3. On the first start the API migrates the demo database, claims it, and seeds it in the background
   (about a minute); sign-in says "being prepared" until then.
4. Give judges the site's `/explore-demo` address and the `DEMO_LOGIN_EMAIL` / `DEMO_LOGIN_PASSWORD` pair.

Signing in to the demo replaces the browser's sign-in cookie, so a staff member who tries the demo is
signed out of their real account in that browser (the page says so).

To switch the demo off, remove `DEMO_MODE` (the demo database is then simply unused).

### Settings

| Variable | Default | |
|---|---|---|
| `DEMO_MODE` | `false` | `true` offers the demo at `/explore-demo` |
| `DEMO_BUNONBRAIN_DB` | — | the demo database; required when on, never the production one |
| `DEMO_LOGIN_EMAIL`, `DEMO_LOGIN_PASSWORD` | — | required when on |
| `DEMO_SESSION_HOURS` | 4 | absolute session length, 1–24 |
| `DEMO_MAX_SESSIONS` | 300 | active reviewer sessions at once |
| `DEMO_RESET_TIME` / `DEMO_TIME_ZONE` | `03:00` / `Asia/Dhaka` | daily reset |
| `DEMO_ADMIN_KEY` | unset | ≥ 24 characters; enables the operator endpoints |

### Operator endpoints (header `X-Demo-Admin-Key`)

```
GET  /api/demo/admin/sessions?active=true      list reviewer sessions
POST /api/demo/admin/sessions/{id}/revoke      end one session
POST /api/demo/admin/sessions/revoke-all       end every session
POST /api/demo/admin/reset                     restore the baseline now
```

They answer 404 without the key, without a configured key, and when the demo is off.

## 4. Tests

- `DemoUnitTests.cs`: settings, demo accounts, the demo route, the production-database check, guard rules,
  and on a database-free host with the demo on: production requests untouched, demo tokens need a live
  session acting as that account, deletes and paid AI refused for demo requests only; on a host with the
  demo off, demo routes 404 and demo tokens are refused.
- `DbDemoTests.cs` (`[DockerFact]`, disposable PostgreSQL, one host with a production and a demo
  database): the baseline is complete; production sign-in works on the same host and never sees demo
  data (and the reverse); one sign-in then any role; switching invalidates the previous role's token;
  refresh, sign-out and operator revocation; create works while account changes, factory deactivation
  and paid AI are refused; the reset restores the baseline, ends sessions and leaves production alone;
  a database with real accounts is never claimed or reset.
- `EndpointAccessMatrixTests.cs` declares the new endpoints.
