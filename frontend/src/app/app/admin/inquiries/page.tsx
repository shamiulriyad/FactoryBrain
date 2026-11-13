"use client";

import { useState } from "react";
import { Inbox, RefreshCw } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { SkeletonRows } from "@/components/ui/Status";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import { P } from "@/lib/access";

type Status = "new" | "in_progress" | "closed";
interface Inquiry {
  id: string;
  kind: string;
  name: string;
  email: string | null;
  phone: string | null;
  company: string | null;
  jobTitle: string | null;
  subject: string | null;
  message: string;
  details: Record<string, string>;
  sourcePath: string | null;
  status: Status;
  createdAt: string;
}
interface InquiryList {
  items: Inquiry[];
  counts: Record<Status, number>;
}

const KINDS = [
  { value: "", label: "All types" },
  { value: "demo", label: "Demo requests" },
  { value: "consultation", label: "Consultations" },
  { value: "contact", label: "Contact" },
  { value: "support", label: "Support" },
  { value: "problem", label: "Problem reports" },
  { value: "feature", label: "Feature requests" },
  { value: "feedback", label: "Feedback" },
  { value: "partnership", label: "Partnerships" },
];
const STATUSES = [
  { value: "", label: "All statuses" },
  { value: "new", label: "New" },
  { value: "in_progress", label: "In progress" },
  { value: "closed", label: "Closed" },
];
const STATUS_TONE: Record<Status, BadgeTone> = { new: "info", in_progress: "warning", closed: "neutral" };
const STATUS_LABEL: Record<Status, string> = { new: "New", in_progress: "In progress", closed: "Closed" };

/** Messages sent from the public website's forms (POST /api/inquiries). Super Admin only. */
function InquiriesInner() {
  const [kind, setKind] = useState("");
  const [status, setStatus] = useState("new");
  const [busy, setBusy] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const qs = new URLSearchParams({ limit: "200", ...(kind && { kind }), ...(status && { status }) }).toString();
  const res = useResource<InquiryList>(`/api/inquiries?${qs}`);

  const setRowStatus = async (id: string, next: Status) => {
    setBusy(id);
    setActionError(null);
    try {
      await apiJson(`/api/inquiries/${id}`, { method: "PATCH", body: { status: next } });
      await res.reload();
    } catch (e) {
      setActionError(errorMessage(e));
    } finally {
      setBusy(null);
    }
  };

  const counts = res.data?.counts;
  return (
    <div className="mx-auto w-full max-w-[1200px] px-4 py-8 md:px-8">
      <PageHeader
        eyebrow="Administration"
        title="Website inquiries"
        description="Demo requests, contact and support messages, problem reports, feature requests, feedback and partnership enquiries sent from the public website."
        actions={
          <Button variant="secondary" onClick={() => res.reload()}>
            <RefreshCw size={15} aria-hidden /> Refresh
          </Button>
        }
      />
      {counts && (
        <p className="mt-4 text-caption text-fg-secondary">
          {counts.new} new · {counts.in_progress} in progress · {counts.closed} closed
        </p>
      )}
      <div className="mt-6 flex flex-wrap gap-3">
        <FilterSelect label="Status" value={status} onChange={setStatus}>
          {STATUSES.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </FilterSelect>
        <FilterSelect label="Type" value={kind} onChange={setKind}>
          {KINDS.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </FilterSelect>
      </div>
      {actionError && (
        <Callout tone="danger" className="mt-4">
          {actionError}
        </Callout>
      )}
      <div className="mt-6">
        {res.error && !res.data ? (
          <Callout tone="danger">{errorMessage(res.error)}</Callout>
        ) : !res.data ? (
          <Panel>
            <SkeletonRows rows={5} />
          </Panel>
        ) : res.data.items.length === 0 ? (
          <EmptyState icon={<Inbox size={20} aria-hidden />} title="No inquiries" body="Nothing matches these filters." />
        ) : (
          <ul className="space-y-3">
            {res.data.items.map((q) => (
              <li key={q.id}>
                <Panel className="p-5">
                  <div className="flex flex-wrap items-start justify-between gap-3">
                    <div className="min-w-0">
                      <div className="flex flex-wrap items-center gap-2">
                        <Badge tone={STATUS_TONE[q.status]}>{STATUS_LABEL[q.status]}</Badge>
                        <Badge tone="neutral">{KINDS.find((k) => k.value === q.kind)?.label ?? q.kind}</Badge>
                        <span className="text-caption text-fg-tertiary">{formatDateTime(q.createdAt)}</span>
                        {q.sourcePath && <span className="font-mono text-caption text-fg-tertiary">{q.sourcePath}</span>}
                      </div>
                      <p className="mt-2 text-body font-semibold text-fg-primary">
                        {q.name}
                        {q.jobTitle && <span className="font-normal text-fg-secondary"> · {q.jobTitle}</span>}
                        {q.company && <span className="font-normal text-fg-secondary"> · {q.company}</span>}
                      </p>
                      <p className="text-caption text-fg-secondary">
                        {q.email && (
                          <a href={`mailto:${q.email}`} className="text-accent underline-offset-2 hover:underline">
                            {q.email}
                          </a>
                        )}
                        {q.email && q.phone && " · "}
                        {q.phone}
                      </p>
                    </div>
                    <div className="flex gap-2">
                      {q.status !== "in_progress" && q.status !== "closed" && (
                        <Button size="sm" variant="secondary" disabled={busy === q.id} onClick={() => setRowStatus(q.id, "in_progress")}>
                          Start
                        </Button>
                      )}
                      {q.status !== "closed" ? (
                        <Button size="sm" disabled={busy === q.id} onClick={() => setRowStatus(q.id, "closed")}>
                          Close
                        </Button>
                      ) : (
                        <Button size="sm" variant="secondary" disabled={busy === q.id} onClick={() => setRowStatus(q.id, "new")}>
                          Reopen
                        </Button>
                      )}
                    </div>
                  </div>
                  {q.subject && <p className="mt-3 text-body font-medium text-fg-primary">{q.subject}</p>}
                  <p className="mt-2 whitespace-pre-wrap text-body text-fg-secondary">{q.message}</p>
                  {Object.keys(q.details).length > 0 && (
                    <dl className="mt-3 flex flex-wrap gap-x-6 gap-y-1 text-caption">
                      {Object.entries(q.details).map(([k, v]) => (
                        <div key={k}>
                          <dt className="inline text-fg-tertiary">{k}: </dt>
                          <dd className="inline text-fg-primary">{v}</dd>
                        </div>
                      ))}
                    </dl>
                  )}
                </Panel>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}

export default function InquiriesPage() {
  return (
    <RequirePermission perms={[P.InquiriesManage]}>
      <InquiriesInner />
    </RequirePermission>
  );
}
