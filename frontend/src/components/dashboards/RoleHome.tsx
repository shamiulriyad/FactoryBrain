"use client";

// The home page: one dashboard per role. Each shows what that role does every
// day (and nothing it can't use), built from the same permissions and factory /
// line scope the API enforces. A person with several roles gets the dashboard of
// their highest-ranked one.

import { Activity, AlertTriangle, BarChart3, Building2, CalendarCheck, CalendarDays, Cctv, ClipboardList, Cpu, FileText, Gauge, IdCard, KeyRound, Layers, MonitorDot, ScrollText, ShieldCheck, Target, UserCog, UsersRound } from "lucide-react";
import { Badge } from "@/components/ui/Badge";
import { Skeleton } from "@/components/ui/Skeleton";
import { StatCard } from "@/components/ui/StatCard";
import { Actions, Card, Empty, Hero, NoFactory, OverviewGroups, Section, useHome, useOverview, type Action } from "@/components/dashboards/kit";
import { OpenExceptions, PlanToday } from "@/components/dashboards/blocks";
import { P } from "@/lib/access";
import { useResource } from "@/lib/useResource";
import { usePendingCount } from "@/lib/useFloorStatus";
import { dhaka } from "@/lib/monitoring";
import { useAuthStore } from "@/lib/auth";

const I = 17;

const A = {
  users: { href: "/app/admin/users", icon: <UserCog size={I} />, title: "Users", body: "Create accounts, set roles and factory scope.", perms: [P.UsersView] },
  roles: { href: "/app/admin/roles", icon: <KeyRound size={I} />, title: "Roles & permissions", body: "See what every role may do.", perms: [P.RolesView] },
  factories: { href: "/app/admin/factories", icon: <Building2 size={I} />, title: "Factories & lines", body: "Factories, floors, departments and production lines.", perms: [P.FactoriesManage, P.LinesManage, P.DepartmentsManage] },
  audit: { href: "/app/audit", icon: <ScrollText size={I} />, title: "Audit log", body: "Who changed what, and when.", perms: [P.AuditView] },
  planning: { href: "/app/planning", icon: <Target size={I} />, title: "Planning & targets", body: "Orders, allocations, daily plans and targets.", perms: [P.PlanningView] },
  orders: { href: "/app/planning/orders", icon: <ClipboardList size={I} />, title: "Production orders", body: "Register orders and follow their status.", perms: [P.PlanningView] },
  plans: { href: "/app/planning/plans", icon: <CalendarDays size={I} />, title: "Daily plans", body: "Prepare, approve and publish today's plan.", perms: [P.PlanningView] },
  targets: { href: "/app/planning/targets", icon: <Target size={I} />, title: "Targets", body: "Line, shift and hourly targets with their history.", perms: [P.PlanningView] },
  exceptions: { href: "/app/planning/exceptions", icon: <AlertTriangle size={I} />, title: "Delays & exceptions", body: "Behind-target, capacity and missing-data alerts.", perms: [P.PlanningView] },
  reports: { href: "/app/planning/reports", icon: <FileText size={I} />, title: "Plan vs actual reports", body: "Daily, shift, order and delivery reports.", perms: [P.PlanningView] },
  analytics: { href: "/app/analytics", icon: <BarChart3 size={I} />, title: "Factory analytics", body: "Production, machines, workforce and insights.", perms: [P.ProductionView, P.MonitoringView, P.DevicesView, P.AttendanceView, P.ReportsView] },
  monitoring: { href: "/app/monitoring", icon: <MonitorDot size={I} />, title: "Monitoring room", body: "Live events, alerts and cameras.", perms: [P.MonitoringView, P.DevicesView] },
  alerts: { href: "/app/activity", icon: <AlertTriangle size={I} />, title: "Alerts", body: "Floor alerts to acknowledge or escalate.", perms: [P.MonitoringView, P.ProductionView] },
  floorMap: { href: "/app/floor-map", icon: <Layers size={I} />, title: "Live floor map", body: "Floors and lines at a glance.", perms: [P.MonitoringView, P.ProductionView] },
  floorBoard: { href: "/app/overview", icon: <Gauge size={I} />, title: "Floor board", body: "Line efficiency, decisions and machine health.", perms: [P.ProductionView, P.MonitoringView, P.ReportsView] },
  approvals: { href: "/app/approvals", icon: <ShieldCheck size={I} />, title: "Approvals", body: "Decide recommendations waiting for you.", perms: [P.ApprovalsView] },
  attendance: { href: "/app/attendance", icon: <CalendarCheck size={I} />, title: "Attendance & shifts", body: "Check-ins, shifts, breaks and overtime.", perms: [P.AttendanceView, P.OvertimeView] },
  assignments: { href: "/app/assignments", icon: <UsersRound size={I} />, title: "Workstation assignments", body: "Who works where on each line.", perms: [P.WorkersView] },
  workers: { href: "/app/workers", icon: <IdCard size={I} />, title: "Workers", body: "Directory, profiles, cards and status.", perms: [P.WorkersView] },
  devices: { href: "/app/cameras-devices", icon: <Cctv size={I} />, title: "Cameras & devices", body: "Health, sources, zones and the simulator.", perms: [P.CamerasView, P.DevicesView] },
  sensors: { href: "/app/sensors", icon: <Cpu size={I} />, title: "Sensors", body: "Telemetry, RFID and machine readings.", perms: [P.DevicesView, P.MonitoringView] },
  setup: { href: "/app/planning/setup", icon: <Activity size={I} />, title: "Planning setup", body: "Output counters and capacity assumptions per line.", perms: [P.PlanningSetup] },
} satisfies Record<string, Action>;

type Home = ReturnType<typeof useHome>;

/** Super Admin and Factory Admin: the whole system's state and its administration. */
function AdminHome({ home, platform }: { home: Home; platform: boolean }) {
  const overview = useOverview(home.factoryId);
  const users = useResource<{ total: number }>("/api/users?pageSize=1");
  const factories = useResource<unknown[]>(platform ? "/api/factories" : null);
  const pending = usePendingCount();
  return (
    <>
      <Hero home={home} subtitle={platform ? "Every factory on the platform: people, access and how each plant is running." : "Your factory's people, access and how it is running."} />
      <Section title="System" testId="home-admin-counts">
        <div className="grid gap-3 grid-cols-2 xl:grid-cols-4">
          <StatCard label="Users" value={users.data ? users.data.total : "…"} hint="accounts you can manage" href="/app/admin/users" testId="home-users" />
          {platform && <StatCard label="Factories" value={factories.data ? factories.data.length : "…"} hint="on the platform" href="/app/admin/factories" testId="home-factories" />}
          <StatCard label="Waiting for approval" value={pending} hint="recommendations" href="/app/approvals" testId="home-approvals" />
        </div>
      </Section>
      {home.factoryId && <Section title="Factory now" hint={home.factory?.name}><OverviewGroups overview={overview} ids={["production", "workforce", "machines", "alerts"]} testId="home-kpis" /></Section>}
      <Actions items={[A.users, A.roles, A.factories, A.audit, A.planning, A.analytics, A.reports, A.monitoring]} title="Administer" />
    </>
  );
}

/** Factory Manager: today's plan against output, deadlines, delays, people and machines. */
function ManagerHome({ home }: { home: Home }) {
  const overview = useOverview(home.factoryId);
  const pending = usePendingCount();
  return (
    <>
      <Hero home={home} subtitle="Today's plan against what the lines have counted, what is late, and who and what is available." />
      <PlanToday factoryId={home.factoryId} />
      <OpenExceptions factoryId={home.factoryId} />
      <Section title="People and machines" hint={home.factory?.name}><OverviewGroups overview={overview} ids={["workforce", "overtime", "machines", "alerts"]} testId="home-kpis" /></Section>
      {pending > 0 && <Section title="Decisions"><StatCard label="Waiting for approval" value={pending} href="/app/approvals" hint="open the queue" testId="home-approvals" /></Section>}
      <Actions items={[A.plans, A.orders, A.targets, A.approvals, A.attendance, A.reports, A.setup, A.analytics]} />
    </>
  );
}

/** Production Supervisor: only their own lines. */
function SupervisorHome({ home }: { home: Home }) {
  const overview = useOverview(home.factoryId);
  const lineIds = useAuthStore((s) => s.user?.lineIds ?? []);
  return (
    <>
      <Hero home={home} subtitle={lineIds.length ? `Your lines: ${lineIds.join(", ")}. Target, output and what needs a look.` : "Target, output and what needs a look on your lines."} />
      <PlanToday factoryId={home.factoryId} />
      <OpenExceptions factoryId={home.factoryId} />
      <Section title="Your lines now"><OverviewGroups overview={overview} ids={["workforce", "machines", "production"]} testId="home-kpis" /></Section>
      <Actions items={[A.targets, A.exceptions, A.assignments, A.attendance, A.alerts, A.floorBoard]} />
    </>
  );
}

/** Monitoring Operator: what is happening now and what is raised. */
function OperatorHome({ home }: { home: Home }) {
  const overview = useOverview(home.factoryId);
  return (
    <>
      <Hero home={home} subtitle="What is running, what is raised, and today's published plan." />
      <Section title="Right now"><OverviewGroups overview={overview} ids={["alerts", "machines"]} testId="home-kpis" /></Section>
      <PlanToday factoryId={home.factoryId} compact />
      <Actions items={[A.monitoring, A.alerts, A.floorMap, A.devices, A.floorBoard, A.analytics]} />
    </>
  );
}

/** HR / Attendance Officer. */
function HrHome({ home }: { home: Home }) {
  const overview = useOverview(home.factoryId);
  return (
    <>
      <Hero home={home} subtitle="Who is in today, who is late or absent, and overtime waiting for a decision." />
      <Section title="Attendance and overtime" hint={home.factory?.name}><OverviewGroups overview={overview} ids={["workforce", "overtime"]} testId="home-kpis" /></Section>
      <Actions items={[A.attendance, A.workers, A.assignments, A.analytics]} />
    </>
  );
}

/** IT / Device Administrator: device health and the data sources feeding the system. */
function DeviceHome({ home }: { home: Home }) {
  const overview = useOverview(home.factoryId);
  const coverage = overview.data?.coverage ?? [];
  return (
    <>
      <Hero home={home} subtitle="Which devices are online, what is reporting, and where data is missing." />
      <Section title="Devices and machines"><OverviewGroups overview={overview} ids={["machines", "alerts"]} testId="home-kpis" /></Section>
      <Section title="Data sources" hint="When each source last delivered data" testId="home-coverage">
        <Card>
          {!overview.data ? <div className="p-4"><Skeleton className="h-16" /></div> : coverage.length === 0 ? <Empty>No data sources reported.</Empty> : (
            <table className="w-full text-caption">
              <thead><tr className="text-left text-fg-tertiary"><th className="px-4 py-2 font-medium">Source</th><th className="px-3 py-2 font-medium text-right">Records</th><th className="px-3 py-2 font-medium">Last record</th><th className="px-4 py-2 font-medium">Status</th></tr></thead>
              <tbody>{coverage.map((c) => (
                <tr key={c.source} className="border-t border-border-subtle">
                  <td className="px-4 py-2 font-medium text-fg-primary">{c.label}</td><td className="px-3 py-2 text-right tabular-nums">{c.records.toLocaleString("en-US")}</td>
                  <td className="px-3 py-2">{c.lastRecordAt ? dhaka(c.lastRecordAt) : "never"}</td>
                  <td className="px-4 py-2"><Badge tone={c.availability === "OK" ? "success" : c.availability === "PARTIAL" ? "warning" : "neutral"}>{c.availability === "OK" ? "Reporting" : c.availability === "PARTIAL" ? "Partial" : "No data"}</Badge></td>
                </tr>))}</tbody>
            </table>
          )}
        </Card>
      </Section>
      <Actions items={[A.devices, A.sensors, A.monitoring, A.analytics]} />
    </>
  );
}

/** A role with permissions but no dedicated dashboard: the pages it can open. */
function GenericHome({ home }: { home: Home }) {
  return (
    <>
      <Hero home={home} subtitle="The pages your role can open." />
      <Actions items={[A.planning, A.analytics, A.monitoring, A.attendance, A.workers, A.devices]} />
    </>
  );
}

export default function RoleHome() {
  const home = useHome();
  const role = String(home.user?.role ?? "");
  const dashboard = role === "SuperAdmin" ? "admin-platform" : role === "FactoryAdmin" ? "admin" : role === "FactoryManager" ? "manager" : role === "ProductionSupervisor" ? "supervisor"
    : role === "MonitoringOperator" ? "operator" : role === "HrOfficer" ? "hr" : role === "DeviceAdmin" ? "device" : "generic";
  return (
    <div className="page" data-testid="role-home" data-dashboard={dashboard}>
      {!home.factoryId && home.factories !== null && dashboard !== "generic" ? <><Hero home={home} subtitle="" /><NoFactory home={home} /></>
        : home.factories === null ? <NoFactory home={home} />
        : dashboard === "admin-platform" ? <AdminHome home={home} platform />
        : dashboard === "admin" ? <AdminHome home={home} platform={false} />
        : dashboard === "manager" ? <ManagerHome home={home} />
        : dashboard === "supervisor" ? <SupervisorHome home={home} />
        : dashboard === "operator" ? <OperatorHome home={home} />
        : dashboard === "hr" ? <HrHome home={home} />
        : dashboard === "device" ? <DeviceHome home={home} />
        : <GenericHome home={home} />}
    </div>
  );
}
