"use client";

// Worker identity views: badges, the per-track identity card, the case drawer
// with its evidence, history and review actions, and the reader mapping
// dialog. A Worker ID is shown only for a VERIFIED, currently valid
// association; everything else says why not.

import { useEffect, useState } from "react";
import { BadgeCheck, CircleHelp, CircleSlash, Fingerprint, ShieldAlert } from "lucide-react";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Drawer } from "@/components/ui/Drawer";
import { Field, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { apiJson, errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { dhaka, dhakaTime } from "@/lib/monitoring";
import { useResource } from "@/lib/useResource";
import {
  IDENTITY_LABEL, IDENTITY_TONE, PRECISIONS,
  type IdentityDetail, type RfidReader, type TrackIdentity
} from "@/lib/identity";

export function IdentityBadge({ status, simulated }: { status: string; simulated?: boolean }) {
  const icon = status === "VERIFIED" ? <BadgeCheck size={11} aria-hidden /> : status === "AMBIGUOUS" || status === "MANUAL_REVIEW" ? <ShieldAlert size={11} aria-hidden />
    : status === "REVOKED" ? <CircleSlash size={11} aria-hidden /> : <CircleHelp size={11} aria-hidden />;
  return (
    <span className="inline-flex items-center gap-1">
      <Badge tone={IDENTITY_TONE[status] ?? "neutral"} testId={`identity-${status}`}><span className="inline-flex items-center gap-1">{icon}{IDENTITY_LABEL[status] ?? status}</span></Badge>
      {simulated && <span className="text-[10px] font-semibold text-[var(--risk-medium)]" title="Built from SIMULATED evidence">SIMULATED</span>}
    </span>
  );
}

/** The illustrated "Track / Identity / Worker / Workstation" block, for one track. */
export function TrackIdentityCard({ t, onOpen }: { t: TrackIdentity; onOpen?: (associationId: string) => void }) {
  const review = t.identity === "AMBIGUOUS" || t.identity === "MANUAL_REVIEW";
  return (
    <div className={cn("rounded-[var(--radius-control)] border px-3 py-2 text-caption", review ? "border-[var(--risk-medium)]" : "border-border-subtle")}
      data-testid="track-identity" data-identity={t.identity}>
      <dl className="grid grid-cols-[6.5rem_1fr] gap-x-2 gap-y-0.5">
        <dt className="text-fg-tertiary">Track ID</dt><dd className="font-mono">{t.trackId}</dd>
        <dt className="text-fg-tertiary">Identity</dt><dd><IdentityBadge status={t.identity} simulated={t.isSimulated} /></dd>
        {t.identity === "VERIFIED" && t.worker ? (
          <>
            <dt className="text-fg-tertiary">Worker</dt><dd className="font-medium">{t.worker.code}{t.worker.name ? <span className="text-fg-tertiary"> · {t.worker.name}</span> : null}</dd>
            <dt className="text-fg-tertiary">Workstation</dt><dd>{t.workstation?.code ?? "—"}</dd>
          </>
        ) : review ? (
          <><dt className="text-fg-tertiary">Action</dt><dd>Manual review required</dd></>
        ) : (
          <><dt className="text-fg-tertiary">Worker</dt><dd className="text-fg-secondary">Not confirmed</dd></>
        )}
        <dt className="text-fg-tertiary">Zone</dt><dd>{t.zone?.name ?? "—"}</dd>
      </dl>
      {t.associationId && onOpen && (
        <button type="button" className="mt-1 text-[var(--accent)] hover:underline" onClick={() => onOpen(t.associationId!)}>
          {t.associationCode} · evidence{review ? " and review" : ""}
        </button>
      )}
    </div>
  );
}

const EFFECT_TONE: Record<string, string> = { SUPPORTS: "text-[var(--risk-low,#16a34a)]", CONTRADICTS: "text-[var(--risk-high)]", CONTEXT: "text-fg-tertiary" };
const DECISION_LABEL: Record<string, string> = { CONFIRM: "Confirm worker", REJECT: "Reject", AMBIGUOUS: "Mark ambiguous", REVOKE: "Revoke" };

/** One association: its evidence, history and (for reviewers) the decision form. */
export function IdentityCaseDrawer({ id, onClose, onChanged }: { id: string | null; onClose: () => void; onChanged?: () => void }) {
  const detail = useResource<IdentityDetail>(id ? `/api/identity/associations/${id}` : null);
  const d = detail.data;
  const toast = useToast();
  const [decision, setDecision] = useState("");
  const [worker, setWorker] = useState("");
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { setDecision(""); setWorker(""); setReason(""); setError(null); }, [id]);

  async function submit() {
    if (!d) return;
    setBusy(true);
    setError(null);
    try {
      await apiJson(`/api/identity/associations/${d.association.id}/review`, {
        method: "POST", body: { decision, reason, workerId: decision === "CONFIRM" ? worker || null : null, expectedVersion: d.association.version }
      });
      toast.push({ tone: "success", title: `${d.association.code}: ${DECISION_LABEL[decision] ?? decision}` });
      setDecision("");
      setReason("");
      await detail.reload();
      onChanged?.();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  const a = d?.association;
  return (
    <Drawer open={!!id} onClose={onClose} title={a ? `${a.code} · track ${a.trackId}` : "Identity case"} width="w-[34rem]">
      {!d || !a ? <div className="p-4"><SkeletonRows rows={5} /></div> : (
        <div className="p-4 space-y-4 text-caption" data-testid="identity-case">
          <div className="flex flex-wrap items-center gap-2">
            <IdentityBadge status={a.status} simulated={a.isSimulated} />
            <span className="text-fg-secondary">{a.camera.code} · {a.workstation?.code ?? "no workstation"}{a.zone ? ` · ${a.zone.name}` : ""}</span>
            {!d.trackActive && <Badge tone="neutral">Track no longer visible</Badge>}
          </div>
          <p className="text-body text-fg-primary">{a.summary}</p>
          <p className="text-fg-secondary">{a.reasonText}</p>
          {a.status === "VERIFIED" && a.worker && (
            <p>Worker <span className="font-medium">{a.worker.code}</span>{a.worker.name ? ` · ${a.worker.name}` : ""} · valid until {dhaka(a.expiresAt)} (Dhaka) · {a.verificationMethod === "MANUAL_REVIEW" ? "confirmed by a reviewer" : "verified automatically"}</p>
          )}

          <section>
            <h3 className="font-medium text-fg-primary mb-1">Evidence</h3>
            <ul className="space-y-1">
              {d.evidence.map((e) => (
                <li key={e.id} className="flex gap-2">
                  <span className={cn("font-semibold w-24 shrink-0", EFFECT_TONE[e.effect])}>{e.effect.toLowerCase()}</span>
                  <span><span className="text-fg-tertiary">{e.kind.toLowerCase().replace(/_/g, " ")}:</span> {e.detail}</span>
                </li>
              ))}
            </ul>
          </section>

          {d.read && (
            <section>
              <h3 className="font-medium text-fg-primary mb-1">RFID read</h3>
              <p>{d.read.readerCode} · card {d.read.card}{d.read.cardLabel ? ` (${d.read.cardLabel})` : ""} · {d.read.worker?.code ?? "unknown worker"} · {dhaka(d.read.occurredAt)}
                {d.read.isSimulated && <span className="ml-1 text-[var(--risk-medium)] font-semibold">SIMULATED</span>}</p>
            </section>
          )}

          {d.candidates.length > 0 && (
            <section>
              <h3 className="font-medium text-fg-primary mb-1">Candidate workers (from the evidence)</h3>
              <ul className="space-y-0.5">
                {d.candidates.map((c) => <li key={c.worker.id}>{c.worker.code}{c.worker.name ? ` · ${c.worker.name}` : ""} — assigned to {c.workstation ?? "no workstation"}</li>)}
              </ul>
              {d.candidateTracks.length > 1 && <p className="text-fg-tertiary mt-1">Tracks in the zone at the time: {d.candidateTracks.join(", ")}</p>}
            </section>
          )}

          <section>
            <h3 className="font-medium text-fg-primary mb-1">History</h3>
            <ol className="space-y-1">
              {d.history.map((h) => (
                <li key={h.id}>
                  <span className="tabular-nums text-fg-tertiary">{dhakaTime(h.at)}</span> · {h.action.toLowerCase().replace(/_/g, " ")} {h.fromStatus ? `${h.fromStatus} → ` : ""}{h.toStatus}
                  {h.worker && <> · {h.worker.code}</>} · <span className="text-fg-secondary">{h.isManual ? h.actorEmail : "identity engine"}</span>
                  <p className="text-fg-tertiary">{h.reason}</p>
                </li>
              ))}
            </ol>
          </section>

          {d.canReview && d.allowedDecisions.length > 0 && (
            <section className="border-t border-border-subtle pt-3 space-y-3" data-testid="identity-review">
              <h3 className="font-medium text-fg-primary">Review</h3>
              <Field label="Decision">
                {(ids) => (
                  <Select id={ids.id} value={decision} onChange={(e) => setDecision(e.target.value)} data-testid="identity-decision">
                    <option value="">Choose…</option>
                    {d.allowedDecisions.map((x) => <option key={x} value={x}>{DECISION_LABEL[x] ?? x}</option>)}
                  </Select>
                )}
              </Field>
              {decision === "CONFIRM" && (
                <Field label="Worker" hint="Only a worker the evidence points to can be confirmed.">
                  {(ids) => (
                    <Select id={ids.id} value={worker} onChange={(e) => setWorker(e.target.value)}>
                      <option value="">Choose…</option>
                      {d.candidates.map((c) => <option key={c.worker.id} value={c.worker.id}>{c.worker.code}{c.worker.name ? ` · ${c.worker.name}` : ""}</option>)}
                    </Select>
                  )}
                </Field>
              )}
              <Field label="Reason" hint="What you checked (e.g. confirmed in person by the line supervisor). Kept in the history and audit log.">
                {(ids) => <TextArea id={ids.id} rows={3} value={reason} onChange={(e) => setReason(e.target.value)} />}
              </Field>
              {error && <Callout tone="danger">{error}</Callout>}
              <Button variant="primary" disabled={busy || !decision || reason.trim().length < 5 || (decision === "CONFIRM" && !worker)} onClick={() => void submit()}>
                {busy ? "Saving…" : "Record decision"}
              </Button>
            </section>
          )}
        </div>
      )}
    </Drawer>
  );
}

type Org = { lines: { id: string; name: string }[]; floors: { id: string; name: string }[] };
type Ws = { id: string; code: string; name?: string | null; lineId: string };

/** Map a reader to a location, with an explicit precision. */
export function ReaderMappingDialog({ reader, onClose, onSaved, org, workstations, zones }: {
  reader: RfidReader | null; onClose: () => void; onSaved: () => void; org: Org; workstations: Ws[]; zones: { id: string; name: string; cameraCode: string }[];
}) {
  const toast = useToast();
  const [precision, setPrecision] = useState("WORKSTATION");
  const [target, setTarget] = useState("");
  const [location, setLocation] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    if (!reader) return;
    const m = reader.mapping;
    setPrecision(m?.precision ?? "WORKSTATION");
    setTarget(m?.workstation?.id ?? m?.cameraZoneId ?? m?.line?.id ?? m?.floorId ?? "");
    setLocation(m?.location ?? "");
    setError(null);
  }, [reader]);

  async function save() {
    if (!reader) return;
    setBusy(true);
    setError(null);
    const body: Record<string, unknown> = { precision, location: location || null };
    if (precision === "WORKSTATION") body.workstationId = target || null;
    if (precision === "ZONE") body.cameraZoneId = target || null;
    if (precision === "LINE") body.lineId = target || null;
    if (precision === "FLOOR") body.floorId = target || null;
    try {
      await apiJson(`/api/identity/readers/${reader.deviceId}/mapping`, { method: "PUT", body });
      toast.push({ tone: "success", title: `${reader.code} mapped` });
      onSaved();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  const options = precision === "WORKSTATION" ? workstations.map((w) => ({ id: w.id, label: `${w.code}${w.name ? ` · ${w.name}` : ""}` }))
    : precision === "ZONE" ? zones.map((z) => ({ id: z.id, label: `${z.cameraCode} · ${z.name}` }))
    : precision === "LINE" ? org.lines.map((l) => ({ id: l.id, label: l.name }))
    : precision === "FLOOR" ? org.floors.map((f) => ({ id: f.id, label: f.name })) : [];
  const hint = PRECISIONS.find((p) => p.id === precision)?.hint;
  return (
    <Modal open={!!reader} onClose={onClose} title={reader ? `Map reader ${reader.code}` : "Map reader"} testId="reader-mapping"
      description="Where the reader is, and how precisely. Only a workstation-level mapping can place a worker at a workstation."
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button><Button variant="primary" disabled={busy || (precision !== "AREA" && !target)} onClick={() => void save()}>{busy ? "Saving…" : "Save mapping"}</Button></>}>
      <div className="space-y-3">
        <Field label="Precision" hint={hint}>
          {(ids) => (
            <Select id={ids.id} value={precision} onChange={(e) => { setPrecision(e.target.value); setTarget(""); }}>
              {PRECISIONS.map((p) => <option key={p.id} value={p.id}>{p.label}</option>)}
            </Select>
          )}
        </Field>
        {precision !== "AREA" && (
          <Field label={precision === "WORKSTATION" ? "Workstation" : precision === "ZONE" ? "Camera zone" : precision === "LINE" ? "Line" : "Floor"}>
            {(ids) => (
              <Select id={ids.id} value={target} onChange={(e) => setTarget(e.target.value)}>
                <option value="">Choose…</option>
                {options.map((o) => <option key={o.id} value={o.id}>{o.label}</option>)}
              </Select>
            )}
          </Field>
        )}
        <Field label="Location note" hint="Optional, e.g. 'under table T-025, left side'.">
          {(ids) => <Input id={ids.id} value={location} onChange={(e) => setLocation(e.target.value)} />}
        </Field>
        {error && <Callout tone="danger">{error}</Callout>}
        <p className="text-caption text-fg-tertiary inline-flex items-center gap-1"><Fingerprint size={12} aria-hidden /> The new mapping applies to reads from now on; earlier reads keep the mapping they were made under.</p>
      </div>
    </Modal>
  );
}
