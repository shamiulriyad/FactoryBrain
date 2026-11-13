"use client";

// Building blocks for the worker pages: status badge, avatar (photo or
// initials), the factory choice for the signed-in user, department / floor /
// line option lists, and the status-change dialog. Every list of options
// comes from the API, so a picker never offers something the API would refuse.

import { useEffect, useMemo, useState } from "react";
import { Badge } from "@/components/ui/Badge";
import { Field, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { ConfirmDialog } from "@/components/ui/Modal";
import { Callout } from "@/components/ui/Callout";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage } from "@/lib/api";
import { can, useAuthStore } from "@/lib/auth";
import { cn } from "@/lib/cn";
import { P, type FactoryDto, type FactoryLine, type FactoryRef } from "@/lib/access";
import {
  STATUS_TONE,
  fetchWorkerPhoto,
  statusReasonRequired,
  type OrgUnit,
  type Worker,
  type WorkerStatus
} from "@/lib/workers";

export function WorkerStatusBadge({ status, className }: { status: string; className?: string }) {
  return (
    <Badge tone={STATUS_TONE[status] ?? "neutral"} dot className={className} testId="worker-status">
      {status}
    </Badge>
  );
}

function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  return ((parts[0]?.[0] ?? "") + (parts.length > 1 ? parts[parts.length - 1][0] : "")).toUpperCase() || "?";
}

/** The worker's photo (fetched with the session, never a public URL) or their initials. */
export function WorkerAvatar({ worker, size = 40, refreshKey }: { worker: Pick<Worker, "id" | "fullName" | "hasPhoto">; size?: number; refreshKey?: unknown }) {
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    if (!worker.hasPhoto) {
      setUrl(null);
      return;
    }
    const ctrl = new AbortController();
    let created: string | null = null;
    fetchWorkerPhoto(worker.id, ctrl.signal)
      .then((u) => {
        created = u;
        setUrl(u);
      })
      .catch(() => setUrl(null));
    return () => {
      ctrl.abort();
      if (created) URL.revokeObjectURL(created);
    };
  }, [worker.id, worker.hasPhoto, refreshKey]);

  const style = { width: size, height: size };
  return url ? (
    // eslint-disable-next-line @next/next/no-img-element -- an object URL from an authenticated fetch
    <img src={url} alt={`Photo of ${worker.fullName}`} style={style} className="shrink-0 rounded-full object-cover border border-border-subtle" />
  ) : (
    <span
      style={{ ...style, fontSize: Math.max(11, size * 0.36) }}
      className="shrink-0 rounded-full bg-[var(--accent-soft)] text-[var(--accent)] border border-[var(--accent-border)] inline-flex items-center justify-center font-semibold"
      aria-hidden
    >
      {initials(worker.fullName)}
    </span>
  );
}

/**
 * The factories the signed-in user works in. Platform users (and anyone with
 * factories.view) get the API's list; everyone else the factories on their
 * session, which is exactly the API's scope for them.
 */
export function useMyFactories(): { factories: FactoryRef[] | null; error: string | null } {
  const user = useAuthStore((s) => s.user);
  const fromApi = can(user, P.FactoriesView, P.UsersView);
  const list = useResource<FactoryDto[]>(fromApi ? "/api/factories" : null);
  if (!fromApi) return { factories: user?.factories ?? [], error: null };
  if (list.error) return { factories: null, error: errorMessage(list.error) };
  if (!list.data) return { factories: null, error: null };
  return { factories: list.data.filter((f) => f.isActive).map((f) => ({ id: f.id, code: f.code, name: f.name })), error: null };
}

/** Departments, floors and lines of one factory (for forms and filters). */
export function useFactoryOrg(factoryId: string | null) {
  const departments = useResource<OrgUnit[]>(factoryId ? `/api/factories/${factoryId}/departments` : null);
  const floors = useResource<OrgUnit[]>(factoryId ? `/api/factories/${factoryId}/floors` : null);
  const lines = useResource<FactoryLine[]>(factoryId ? `/api/factories/${factoryId}/lines` : null);
  return { departments, floors, lines };
}

/** Text with a muted em dash when empty. */
export function Val({ children, mono }: { children?: React.ReactNode; mono?: boolean }) {
  const empty = children === null || children === undefined || children === "";
  return empty ? <span className="text-fg-tertiary">—</span> : <span className={cn(mono && "font-mono")}>{children}</span>;
}

/** A labelled value in a definition grid. */
export function Fact({ label, children, testId }: { label: string; children?: React.ReactNode; testId?: string }) {
  return (
    <div className="min-w-0" data-testid={testId}>
      <dt className="text-caption text-fg-tertiary">{label}</dt>
      <dd className="mt-0.5 text-body text-fg-primary break-words">{children}</dd>
    </div>
  );
}

/** Pick one of the allowed next statuses, with the reason the server requires. */
export function StatusChangeDialog({
  open,
  worker,
  allowed,
  onClose,
  onChanged
}: {
  open: boolean;
  worker: Pick<Worker, "id" | "workerCode" | "fullName" | "status">;
  allowed: WorkerStatus[];
  onClose: () => void;
  onChanged: (w: Worker) => void;
}) {
  const [to, setTo] = useState<string>("");
  const [reason, setReason] = useState("");
  const [effectiveOn, setEffectiveOn] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (open) {
      setTo(allowed[0] ?? "");
      setReason("");
      setEffectiveOn("");
      setError(null);
    }
  }, [open, allowed]);

  const needsReason = to ? statusReasonRequired(worker.status, to) : false;
  const separating = to === "Resigned" || to === "Terminated";
  const missing = !to || (needsReason && !reason.trim());

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const w = await apiJson<Worker>(`/api/workers/${worker.id}/status`, {
        method: "PATCH",
        body: { status: to, reason: reason.trim() || null, effectiveOn: effectiveOn || null }
      });
      onChanged(w);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  const options = useMemo(() => allowed, [allowed]);
  return (
    <ConfirmDialog
      open={open}
      title={`Change status of ${worker.workerCode}`}
      description={`${worker.fullName} is currently ${worker.status}. Every change is kept in the status history and the audit log.`}
      confirmLabel={busy ? "Saving…" : to ? `Set to ${to}` : "Change status"}
      tone={separating ? "danger" : "primary"}
      busy={busy}
      confirmDisabled={missing}
      onConfirm={() => void save()}
      onCancel={onClose}
      testId="worker-status-dialog"
      confirmTestId="worker-status-confirm"
    >
      {options.length === 0 ? (
        <p className="text-caption text-fg-secondary">No status change is possible from {worker.status}.</p>
      ) : (
        <div className="space-y-3">
          <Field label="New status">
            {({ id }) => (
              <Select id={id} value={to} onChange={(e) => setTo(e.target.value)} data-testid="worker-status-select">
                {options.map((s) => <option key={s} value={s}>{s}</option>)}
              </Select>
            )}
          </Field>
          <Field label={needsReason ? "Reason (required)" : "Reason (optional)"} hint="Shown in the worker's status history.">
            {({ id, describedBy }) => (
              <TextArea id={id} aria-describedby={describedBy} rows={3} maxLength={500} value={reason}
                onChange={(e) => setReason(e.target.value)} data-testid="worker-status-reason" />
            )}
          </Field>
          <Field label="Effective date (optional)">
            {({ id }) => <Input id={id} type="date" value={effectiveOn} onChange={(e) => setEffectiveOn(e.target.value)} />}
          </Field>
          {separating && (
            <Callout tone="warning">
              The worker record is kept with its Worker ID and history. Active RFID / NFC cards are deactivated at once.
              {to === "Terminated" ? " This is recorded as a termination." : ""}
            </Callout>
          )}
          {error && <Callout tone="danger" role="alert">{error}</Callout>}
        </div>
      )}
    </ConfirmDialog>
  );
}
