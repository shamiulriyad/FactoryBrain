"use client";

// A worker's own page: their worker profile and assigned workstation
// (GET /api/workers/me). Attendance, shift, activity and break records are
// added here when those modules ship; the permission (self.view) is in place.

import Link from "next/link";
import { CalendarClock, IdCard, MapPin } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useResource } from "@/lib/useResource";
import { errorMessage, isApiError } from "@/lib/api";
import { formatDate } from "@/lib/format";
import { useT } from "@/lib/useT";
import { P, type WorkerDto } from "@/lib/access";

export default function MyProfilePage() {
  return (
    <RequirePermission perms={[P.SelfView]} what="Your worker profile">
      <Mine />
    </RequirePermission>
  );
}

function Mine() {
  const { locale } = useT();
  const me = useResource<WorkerDto>("/api/workers/me");
  const notLinked = isApiError(me.error) && me.error.status === 404;

  return (
    <div className="page page-narrow" data-testid="me-page">
      <PageHeader eyebrow="My workspace" title={me.data?.fullName ?? "My profile"} description="Your worker profile, workstation and records." />
      <div className="mt-6 space-y-4">
        {me.loading ? (
          <SkeletonRows rows={3} rowHeight="h-14" />
        ) : notLinked ? (
          <EmptyState icon={<IdCard size={18} />} title="No worker profile linked" body="Your login isn't linked to a worker profile yet. Ask HR or your Factory Admin." />
        ) : me.error || !me.data ? (
          <ErrorState title="Couldn't load your profile" body={me.error ? errorMessage(me.error) : undefined} retryLabel="Try again" onRetry={() => void me.reload()} />
        ) : (
          <>
            <Panel title="Profile" icon={<IdCard size={14} />}>
              <dl className="grid gap-3 sm:grid-cols-3 text-caption">
                <Item label="Employee code"><span className="font-mono">{me.data.employeeCode}</span></Item>
                <Item label="Designation">{me.data.designation ?? "—"}</Item>
                <Item label="Status"><Badge tone={me.data.isActive ? "success" : "neutral"}>{me.data.isActive ? "Active" : "Inactive"}</Badge></Item>
                <Item label="Grade">{me.data.grade ?? "—"}</Item>
                <Item label="Joined">{me.data.joinedOn ? formatDate(me.data.joinedOn, locale) : "—"}</Item>
                <Item label="Phone">{me.data.phone ?? "—"}</Item>
              </dl>
            </Panel>
            <Panel title="Assigned workstation" icon={<MapPin size={14} />}>
              <p className="text-body text-fg-primary" data-testid="my-workstation">
                {me.data.lineId ? <>Line <strong>{me.data.lineId}</strong>{me.data.workstation ? <> · workstation <strong>{me.data.workstation}</strong></> : null}</> : "Not assigned yet."}
              </p>
            </Panel>
            <Panel title="Attendance, shift & breaks" icon={<CalendarClock size={14} />}>
              <p className="text-caption text-fg-secondary">
                Your attendance, shift, activity and break records will appear here once the attendance module is connected.
              </p>
            </Panel>
          </>
        )}
        <p className="text-caption text-fg-tertiary">Account and password: <Link href="/app/account" className="text-accent hover:underline">Account settings</Link>.</p>
      </div>
    </div>
  );
}

function Item({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <dt className="text-fg-tertiary">{label}</dt>
      <dd className="mt-0.5 text-fg-primary">{children}</dd>
    </div>
  );
}
