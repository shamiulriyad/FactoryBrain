"use client";

// Worker details: the profile in sections (personal, employment, work,
// credentials & account, status history, activity overview). What appears
// depends on what the API returns for this caller; actions appear only when
// the API's capabilities allow them, and the API checks every one again.
// The activity overview is a set of placeholders: attendance, shifts and
// monitoring arrive with later modules and nothing is invented meanwhile.

import Link from "next/link";
import { useRef, useState } from "react";
import { useParams } from "next/navigation";
import {
  BadgeCheck, CalendarClock, Camera, CreditCard, History, IdCard, KeyRound, MapPin, Pencil, Phone, RefreshCw, Trash2, UserRound, UsersRound
} from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge, RoleBadges } from "@/components/ui/Badge";
import { Field, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Callout } from "@/components/ui/Callout";
import { ConfirmDialog, Modal } from "@/components/ui/Modal";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { generateTemporaryPassword } from "@/components/access/shared";
import { Fact, StatusChangeDialog, Val, WorkerAvatar, WorkerStatusBadge } from "@/components/workers/shared";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage, isApiError } from "@/lib/api";
import { useCan } from "@/lib/auth";
import { formatDateTime } from "@/lib/format";
import { useT } from "@/lib/useT";
import { P } from "@/lib/access";
import {
  CARD_DEACTIVATION_REASONS, CARD_TYPES, formatDay, uploadWorkerPhoto, type Worker, type WorkerCard, type WorkerProfile
} from "@/lib/workers";

export default function WorkerDetailsPage() {
  const { id } = useParams<{ id: string }>();
  const profile = useResource<WorkerProfile>(`/api/workers/${encodeURIComponent(id)}/profile`);
  const notFound = isApiError(profile.error) && profile.error.status === 404;

  if (profile.loading) return <div className="page"><SkeletonRows rows={6} rowHeight="h-24" /></div>;
  if (notFound)
    return (
      <div className="page page-narrow">
        <EmptyState icon={<UsersRound size={18} />} title="Worker not found" body="It doesn't exist, or it is outside the factories and lines you can see." />
        <p className="text-center"><Link href="/app/workers" className="text-accent hover:underline text-caption">Back to the worker directory</Link></p>
      </div>
    );
  if (profile.error || !profile.data)
    return (
      <div className="page page-narrow">
        <ErrorState title="Couldn't load the worker" body={profile.error ? errorMessage(profile.error) : undefined} retryLabel="Try again" onRetry={() => void profile.reload()} />
      </div>
    );
  return <Details profile={profile.data} reload={profile.reload} />;
}

function Details({ profile, reload }: { profile: WorkerProfile; reload: () => Promise<void> }) {
  const { locale } = useT();
  const toast = useToast();
  const w = profile.worker;
  const can = profile.can;
  const [statusOpen, setStatusOpen] = useState(false);
  const [photoKey, setPhotoKey] = useState(0);
  const [photoBusy, setPhotoBusy] = useState(false);
  const [removePhoto, setRemovePhoto] = useState(false);
  const fileRef = useRef<HTMLInputElement>(null);
  const employment = w.detail !== "basic";
  const personal = w.detail === "full";

  async function onPhoto(file: File | undefined) {
    if (!file) return;
    setPhotoBusy(true);
    try {
      await uploadWorkerPhoto(w.id, file);
      toast.push({ tone: "success", title: "Photo updated" });
      setPhotoKey((k) => k + 1);
      await reload();
    } catch (e) {
      toast.push({ tone: "error", title: "Photo not saved", description: errorMessage(e) });
    } finally {
      setPhotoBusy(false);
      if (fileRef.current) fileRef.current.value = "";
    }
  }

  async function deletePhoto() {
    setPhotoBusy(true);
    try {
      await apiJson(`/api/workers/${w.id}/photo`, { method: "DELETE" });
      toast.push({ tone: "success", title: "Photo removed" });
      await reload();
    } catch (e) {
      toast.push({ tone: "error", title: "Photo not removed", description: errorMessage(e) });
    } finally {
      setPhotoBusy(false);
      setRemovePhoto(false);
    }
  }

  return (
    <div className="page" data-testid="worker-details-page">
      <PageHeader
        eyebrow={<Link href="/app/workers" className="hover:underline">← Worker directory</Link>}
        title={
          <span className="flex items-center gap-3 min-w-0">
            <WorkerAvatar worker={w} size={48} refreshKey={photoKey} />
            <span className="min-w-0 truncate">{w.fullName}</span>
          </span>
        }
        meta={
          <>
            <span className="font-mono text-body text-fg-primary px-2 py-0.5 rounded border border-border-subtle" data-testid="worker-code">{w.workerCode}</span>
            <WorkerStatusBadge status={w.status} />
            {w.designation && <Badge>{w.designation}</Badge>}
            <span className="text-caption text-fg-tertiary">{profile.factory.code} · {profile.factory.name}</span>
          </>
        }
        actions={
          <>
            <Button variant="secondary" size="sm" aria-label="Refresh" onClick={() => void reload()}><RefreshCw size={13} /></Button>
            {can.edit && (
              <>
                <input ref={fileRef} type="file" accept="image/jpeg,image/png,image/webp" className="sr-only" aria-label="Upload a photo"
                  onChange={(e) => void onPhoto(e.target.files?.[0])} data-testid="worker-photo-input" />
                <Button variant="secondary" size="sm" iconLeft={<Camera size={13} />} disabled={photoBusy} onClick={() => fileRef.current?.click()}>
                  {photoBusy ? "Uploading…" : w.hasPhoto ? "Change photo" : "Add photo"}
                </Button>
                {w.hasPhoto && (
                  <Button variant="ghost" size="sm" iconLeft={<Trash2 size={13} />} disabled={photoBusy} onClick={() => setRemovePhoto(true)}>Remove photo</Button>
                )}
                <Link href={`/app/workers/${w.id}/edit`}>
                  <Button variant="secondary" size="sm" iconLeft={<Pencil size={13} />} data-testid="worker-edit">Edit</Button>
                </Link>
              </>
            )}
            {can.changeStatus && (
              <Button variant="primary" size="sm" iconLeft={<BadgeCheck size={13} />} onClick={() => setStatusOpen(true)}
                disabled={can.allowedStatuses.length === 0} data-testid="worker-change-status">
                Change status
              </Button>
            )}
          </>
        }
      />

      {w.status !== "Active" && (
        <Callout className="mt-4" tone={w.status === "Terminated" ? "danger" : "warning"} title={`This worker is ${w.status}`} testId="worker-status-callout">
          {w.statusReason ? <>Reason: {w.statusReason}. </> : null}
          {w.statusChangedAt ? <>Since {formatDateTime(w.statusChangedAt, locale)}. </> : null}
          The record and its history are kept.
        </Callout>
      )}

      <div className="mt-6 grid gap-4 lg:grid-cols-2">
        <Panel title="Personal information" icon={<UserRound size={14} />}
          subtitle={personal ? undefined : "Contact and personal details are visible only to HR, administrators and the worker."}>
          <dl className="grid gap-3 sm:grid-cols-2">
            <Fact label="Worker ID" testId="fact-worker-id"><span className="font-mono">{w.workerCode}</span></Fact>
            {employment && <Fact label="Employee / staff number"><Val mono>{w.employeeCode}</Val></Fact>}
            <Fact label="Full name">{w.fullName}</Fact>
            {personal && (
              <>
                <Fact label="Phone"><Val>{w.phone && <a href={`tel:${w.phone}`} className="hover:underline inline-flex items-center gap-1"><Phone size={12} aria-hidden />{w.phone}</a>}</Val></Fact>
                <Fact label="Email"><Val>{w.email}</Val></Fact>
                <Fact label="Gender"><Val>{w.gender}</Val></Fact>
                <Fact label="Date of birth">{formatDay(w.dateOfBirth, locale)}</Fact>
                <Fact label="Present address"><Val>{w.presentAddress}</Val></Fact>
                <Fact label="Emergency contact">
                  <Val>{w.emergencyContactName && <>{w.emergencyContactName}{w.emergencyContactRelation ? ` (${w.emergencyContactRelation})` : ""}{w.emergencyContactPhone ? ` · ${w.emergencyContactPhone}` : ""}</>}</Val>
                </Fact>
              </>
            )}
          </dl>
        </Panel>

        <Panel title="Employment information" icon={<IdCard size={14} />}
          subtitle={employment ? undefined : "Employment details need the View employment details permission."}>
          <dl className="grid gap-3 sm:grid-cols-2">
            <Fact label="Factory">{profile.factory.name}</Fact>
            <Fact label="Department"><Val>{w.department?.name}</Val></Fact>
            <Fact label="Designation"><Val>{w.designation}</Val></Fact>
            <Fact label="Employment status"><WorkerStatusBadge status={w.status} /></Fact>
            {employment && (
              <>
                <Fact label="Joining date">{formatDay(w.joinedOn, locale)}</Fact>
                <Fact label="Employment type"><Val>{w.employmentType}</Val></Fact>
                <Fact label="Grade"><Val>{w.grade}</Val></Fact>
                <Fact label="Registered">{w.createdAt ? formatDateTime(w.createdAt, locale) : "—"}</Fact>
              </>
            )}
          </dl>
        </Panel>

        <Panel title="Work information" icon={<MapPin size={14} />} subtitle="Current placement. Workstation and machine management arrive with later modules.">
          <dl className="grid gap-3 sm:grid-cols-2">
            <Fact label="Production floor"><Val>{w.floor?.name}</Val></Fact>
            <Fact label="Production line"><Val>{w.line ? <>{w.line.name} <span className="font-mono text-caption text-fg-tertiary">{w.line.id}</span></> : w.lineId}</Val></Fact>
            <Fact label="Current workstation"><Val>{w.workstation}</Val></Fact>
            <Fact label="Machine"><Val>{profile.machine}</Val></Fact>
          </dl>
        </Panel>

        <CredentialsPanel profile={profile} reload={reload} />

        {profile.statusHistory && (
          <Panel title="Status history" icon={<History size={14} />} className="lg:col-span-2" flush>
            {profile.statusHistory.length === 0 ? (
              <p className="p-4 text-caption text-fg-tertiary">No status changes recorded.</p>
            ) : (
              <div className="overflow-x-auto">
                <table className="data-table" data-testid="worker-status-history">
                  <thead><tr><th>When</th><th>Change</th><th>Reason</th><th>Effective</th><th>By</th></tr></thead>
                  <tbody>
                    {profile.statusHistory.map((h) => (
                      <tr key={h.id}>
                        <td className="whitespace-nowrap text-caption text-fg-secondary">{formatDateTime(h.changedAt, locale)}</td>
                        <td className="whitespace-nowrap">
                          {h.fromStatus ? <><WorkerStatusBadge status={h.fromStatus} /> <span className="text-fg-tertiary" aria-label="to">→</span> </> : <span className="text-caption text-fg-tertiary">Registered as </span>}
                          <WorkerStatusBadge status={h.toStatus} />
                        </td>
                        <td className="text-caption text-fg-secondary"><Val>{h.reason}</Val></td>
                        <td className="whitespace-nowrap text-caption text-fg-secondary">{formatDay(h.effectiveOn, locale)}</td>
                        <td className="text-caption text-fg-secondary"><Val>{h.changedByEmail}</Val></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </Panel>
        )}

        <Panel title="Activity overview" icon={<CalendarClock size={14} />} className="lg:col-span-2"
          subtitle="These records appear when the attendance, shift and monitoring modules are connected. Nothing is shown until real data exists.">
          <ul className="grid gap-2 sm:grid-cols-2 lg:grid-cols-4" data-testid="worker-activity">
            {profile.activity.map((a) => (
              <li key={a.key} className="surface-2 p-3 rounded-[var(--radius-control)]" data-testid={`activity-${a.key}`}>
                <p className="text-body font-medium text-fg-primary">{a.title}</p>
                <p className="mt-1 text-caption text-fg-tertiary">{a.available ? `${a.items.length} records` : a.note}</p>
                {!a.available && <Badge className="mt-2">Not connected yet</Badge>}
              </li>
            ))}
          </ul>
        </Panel>
      </div>

      <StatusChangeDialog
        open={statusOpen}
        worker={w}
        allowed={can.allowedStatuses}
        onClose={() => setStatusOpen(false)}
        onChanged={(next: Worker) => {
          setStatusOpen(false);
          toast.push({ tone: "success", title: `Status changed to ${next.status}`, description: `${next.workerCode} · ${next.fullName}` });
          void reload();
        }}
      />
      <ConfirmDialog
        open={removePhoto}
        title="Remove the photo?"
        description="The photo file is deleted. You can upload a new one at any time."
        confirmLabel="Remove photo"
        tone="danger"
        busy={photoBusy}
        onConfirm={() => void deletePhoto()}
        onCancel={() => setRemovePhoto(false)}
      />
    </div>
  );
}

/** RFID / NFC cards and the optional login account. */
function CredentialsPanel({ profile, reload }: { profile: WorkerProfile; reload: () => Promise<void> }) {
  const { locale } = useT();
  const toast = useToast();
  const w = profile.worker;
  const can = profile.can;
  const canCreateLogin = useCan(P.UsersCreate);
  const [issueOpen, setIssueOpen] = useState(false);
  const [deactivating, setDeactivating] = useState<WorkerCard | null>(null);
  const [reason, setReason] = useState<string>(CARD_DEACTIVATION_REASONS[0]);
  const [accountOpen, setAccountOpen] = useState(false);
  const [unlinkOpen, setUnlinkOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const separated = w.status === "Resigned" || w.status === "Terminated";

  async function deactivate() {
    if (!deactivating) return;
    setBusy(true);
    try {
      await apiJson(`/api/workers/${w.id}/cards/${deactivating.id}`, { method: "PATCH", body: { reason } });
      toast.push({ tone: "success", title: "Card deactivated" });
      setDeactivating(null);
      await reload();
    } catch (e) {
      toast.push({ tone: "error", title: "Not deactivated", description: errorMessage(e) });
    } finally {
      setBusy(false);
    }
  }

  async function unlink() {
    setBusy(true);
    try {
      await apiJson(`/api/workers/${w.id}/account`, { method: "PUT", body: { userId: null } });
      toast.push({ tone: "success", title: "Login account unlinked", description: "The account still exists; it no longer reaches this worker's records." });
      setUnlinkOpen(false);
      await reload();
    } catch (e) {
      toast.push({ tone: "error", title: "Not unlinked", description: errorMessage(e) });
    } finally {
      setBusy(false);
    }
  }

  const showCards = profile.cards !== null && profile.cards !== undefined;
  const showAccount = w.hasAccount !== undefined && w.hasAccount !== null;
  if (!showCards && !showAccount) return null;

  return (
    <Panel title="Credentials & login" icon={<CreditCard size={14} />}
      subtitle="A card identifies a credential, not the person: the Worker ID stays the worker's identity.">
      {showCards && (
        <div>
          <div className="flex items-center justify-between gap-2">
            <h3 className="text-caption font-medium text-fg-secondary">RFID / NFC cards</h3>
            {can.manageCards && !separated && (
              <Button size="sm" variant="secondary" onClick={() => setIssueOpen(true)} data-testid="worker-issue-card">Issue card</Button>
            )}
          </div>
          {profile.cards!.length === 0 ? (
            <p className="mt-2 text-caption text-fg-tertiary">No card issued.</p>
          ) : (
            <ul className="mt-2 divide-y divide-border-subtle" data-testid="worker-cards">
              {profile.cards!.map((c) => (
                <li key={c.id} className="py-2 flex flex-wrap items-center gap-2">
                  <span className="font-mono text-body text-fg-primary">{c.cardUid}</span>
                  <Badge>{c.cardType}</Badge>
                  {c.label && <span className="text-caption text-fg-tertiary">{c.label}</span>}
                  <Badge tone={c.isActive ? "success" : "neutral"} dot>{c.isActive ? "Active" : c.deactivationReason ?? "Inactive"}</Badge>
                  <span className="text-caption text-fg-tertiary">issued {formatDateTime(c.issuedAt, locale)}</span>
                  {c.isActive && can.manageCards && (
                    <Button size="sm" variant="ghost" className="ml-auto" onClick={() => { setReason(CARD_DEACTIVATION_REASONS[0]); setDeactivating(c); }}>Deactivate</Button>
                  )}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      {showAccount && (
        <div className={showCards ? "mt-4 pt-4 border-t border-border-subtle" : undefined}>
          <h3 className="text-caption font-medium text-fg-secondary flex items-center gap-1.5"><KeyRound size={12} aria-hidden /> Login account</h3>
          {w.hasAccount ? (
            <div className="mt-2 flex flex-wrap items-center gap-2" data-testid="worker-account">
              {profile.account?.email ? <span className="text-body text-fg-primary">{profile.account.email}</span> : <span className="text-body text-fg-primary">Linked</span>}
              {profile.account?.roles && <RoleBadges roles={profile.account.roles} />}
              {profile.account?.isActive === false && <Badge tone="danger">Deactivated</Badge>}
              {profile.account && canCreateLogin && (
                <Link href={`/app/admin/users/${profile.account.userId}`} className="text-caption text-accent hover:underline">Open account</Link>
              )}
              {can.manageAccount && (
                <Button size="sm" variant="ghost" className="ml-auto" onClick={() => setUnlinkOpen(true)}>Unlink</Button>
              )}
            </div>
          ) : (
            <div className="mt-2 flex flex-wrap items-center gap-2">
              <span className="text-caption text-fg-tertiary">No login. Workers don&apos;t need one; give one only if this worker should see their own records.</span>
              {can.manageAccount && canCreateLogin && !separated && (
                <Button size="sm" variant="secondary" className="ml-auto" onClick={() => setAccountOpen(true)} data-testid="worker-create-account">Create login</Button>
              )}
            </div>
          )}
        </div>
      )}

      <IssueCardDialog open={issueOpen} workerId={w.id} onClose={() => setIssueOpen(false)} onIssued={() => { setIssueOpen(false); void reload(); }} />
      <CreateAccountDialog open={accountOpen} worker={w} onClose={() => setAccountOpen(false)} onCreated={() => { setAccountOpen(false); void reload(); }} />
      <ConfirmDialog
        open={!!deactivating}
        title={`Deactivate card ${deactivating?.cardUid ?? ""}?`}
        description="Readers stop accepting it for this worker. The card's history is kept, and a returned card can be issued again."
        confirmLabel="Deactivate card"
        tone="danger"
        busy={busy}
        onConfirm={() => void deactivate()}
        onCancel={() => setDeactivating(null)}
      >
        <Field label="Reason">
          {({ id }) => (
            <Select id={id} value={reason} onChange={(e) => setReason(e.target.value)}>
              {CARD_DEACTIVATION_REASONS.map((r) => <option key={r} value={r}>{r}</option>)}
            </Select>
          )}
        </Field>
      </ConfirmDialog>
      <ConfirmDialog
        open={unlinkOpen}
        title="Unlink the login account?"
        description="The account keeps existing (deactivate it under Users if needed), but it no longer reaches this worker's records."
        confirmLabel="Unlink"
        tone="danger"
        busy={busy}
        onConfirm={() => void unlink()}
        onCancel={() => setUnlinkOpen(false)}
      />
    </Panel>
  );
}

function IssueCardDialog({ open, workerId, onClose, onIssued }: { open: boolean; workerId: string; onClose: () => void; onIssued: () => void }) {
  const toast = useToast();
  const [uid, setUid] = useState("");
  const [type, setType] = useState<string>(CARD_TYPES[0]);
  const [label, setLabel] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const normalised = uid.replace(/[\s:-]/g, "").toUpperCase();
  const valid = /^[A-Z0-9]{4,64}$/.test(normalised);

  async function issue() {
    setBusy(true);
    setError(null);
    try {
      await apiJson(`/api/workers/${workerId}/cards`, { method: "POST", body: { cardUid: uid, cardType: type, label: label.trim() || null } });
      toast.push({ tone: "success", title: "Card issued", description: normalised });
      setUid("");
      setLabel("");
      onIssued();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open={open} onClose={onClose} locked={busy} title="Issue an RFID / NFC card" description="Enter the card ID the reader reports. Spaces, ':' and '-' are ignored."
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="primary" onClick={() => void issue()} disabled={busy || !valid} data-testid="worker-issue-card-confirm">{busy ? "Issuing…" : "Issue card"}</Button>
        </div>
      }>
      <div className="space-y-3">
        <Field label="Card ID" hint={uid && valid ? `Stored as ${normalised}` : "4–64 letters and digits."} error={uid && !valid ? "4–64 letters and digits." : null}>
          {({ id, describedBy, invalid }) => (
            <Input id={id} value={uid} onChange={(e) => setUid(e.target.value)} className="font-mono" aria-invalid={invalid} aria-describedby={describedBy}
              autoComplete="off" data-autofocus data-testid="worker-card-uid" />
          )}
        </Field>
        <Field label="Type">
          {({ id }) => <Select id={id} value={type} onChange={(e) => setType(e.target.value)}>{CARD_TYPES.map((t) => <option key={t}>{t}</option>)}</Select>}
        </Field>
        <Field label="Printed label" hint="Optional, e.g. the number printed on the card.">
          {({ id }) => <Input id={id} value={label} maxLength={80} onChange={(e) => setLabel(e.target.value)} />}
        </Field>
        {error && <Callout tone="danger" role="alert">{error}</Callout>}
      </div>
    </Modal>
  );
}

function CreateAccountDialog({ open, worker, onClose, onCreated }: { open: boolean; worker: Worker; onClose: () => void; onCreated: () => void }) {
  const toast = useToast();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState(() => generateTemporaryPassword());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const valid = /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email.trim()) && password.length >= 10;

  async function create() {
    setBusy(true);
    setError(null);
    try {
      await apiJson(`/api/workers/${worker.id}/account`, { method: "POST", body: { email: email.trim(), temporaryPassword: password } });
      toast.push({ tone: "success", title: "Login created", description: `Give ${worker.fullName} the email and temporary password. It must be changed at first sign-in.` });
      setEmail("");
      setPassword(generateTemporaryPassword());
      onCreated();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open={open} onClose={onClose} locked={busy} title={`Create a login for ${worker.workerCode}`}
      description="The account gets the Worker role only: the worker sees their own profile and nothing else. Name and phone stay on the worker record."
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="primary" onClick={() => void create()} disabled={busy || !valid} data-testid="worker-create-account-confirm">{busy ? "Creating…" : "Create login"}</Button>
        </div>
      }>
      <div className="space-y-3">
        <Field label="Sign-in email">
          {({ id }) => <Input id={id} type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="off" data-autofocus data-testid="worker-account-email" />}
        </Field>
        <Field label="Temporary password" hint="At least 10 characters. Copy it now; it is not shown again.">
          {({ id, describedBy }) => (
            <div className="flex gap-2">
              <Input id={id} value={password} onChange={(e) => setPassword(e.target.value)} className="font-mono" aria-describedby={describedBy} autoComplete="new-password" />
              <Button type="button" variant="secondary" onClick={() => setPassword(generateTemporaryPassword())}>New</Button>
            </div>
          )}
        </Field>
        {error && <Callout tone="danger" role="alert">{error}</Callout>}
      </div>
    </Modal>
  );
}
