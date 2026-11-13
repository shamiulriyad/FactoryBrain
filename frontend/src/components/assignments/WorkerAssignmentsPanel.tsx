"use client";

// The worker profile's assignment section: the current workstation (with
// Assign / Transfer / End for those allowed) and the full timeline.

import Link from "next/link";
import { useState } from "react";
import { ArrowRightLeft, LogOut, MapPinned, UserPlus } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { useCan } from "@/lib/auth";
import { formatDateTime } from "@/lib/format";
import { useT } from "@/lib/useT";
import { P } from "@/lib/access";
import { NOT_PRESENCE_NOTE, duration, type Assignment } from "@/lib/assignments";
import { AssignDialog, AssignmentHistoryTable, EndAssignmentDialog, TransferDialog } from "./shared";

export function WorkerAssignmentsPanel({ worker, onChanged, className }: {
  worker: { id: string; workerCode: string; fullName: string; factoryId: string; status: string; lineId?: string | null };
  onChanged?: () => void;
  className?: string;
}) {
  const { locale } = useT();
  const canAssign = useCan(P.WorkersAssign);
  const history = useResource<Assignment[]>(`/api/workers/${worker.id}/assignments`);
  const current = (history.data ?? []).find((a) => a.status === "Active") ?? null;
  const [assigning, setAssigning] = useState(false);
  const [transferring, setTransferring] = useState(false);
  const [ending, setEnding] = useState(false);
  const done = () => { void history.reload(); onChanged?.(); };

  return (
    <Panel title="Workstation assignment" icon={<MapPinned size={14} />} subtitle={NOT_PRESENCE_NOTE} flush className={className} testId="worker-assignments">
      {history.loading ? (
        <div className="p-4"><SkeletonRows rows={2} rowHeight="h-12" /></div>
      ) : history.error ? (
        <ErrorState className="m-4" title="Couldn't load assignments" body={errorMessage(history.error)} retryLabel="Try again" onRetry={() => void history.reload()} />
      ) : (
        <>
          <div className="p-4 flex flex-wrap items-center gap-3 border-b border-border-subtle" data-testid="worker-current-assignment">
            {current ? (
              <span className="min-w-0">
                <span className="text-caption text-fg-tertiary">Current workstation</span>
                <span className="block text-body text-fg-primary">
                  <Link href={`/app/assignments/workstations/${current.workstation.id}`} className="font-mono font-semibold hover:underline">{current.workstation.code}</Link>
                  {" · "}{current.line.name} · {current.department.name}{current.floor ? ` · ${current.floor.name}` : ""}
                  {current.machine ? ` · machine ${current.machine.code}` : ""}
                </span>
                <span className="block text-caption text-fg-tertiary">Since {formatDateTime(current.startAt, locale)} ({duration(current.startAt)}) · assigned by {current.assignedByEmail}</span>
              </span>
            ) : (
              <span className="text-caption text-fg-secondary">
                No workstation assigned.{worker.status !== "Active" ? ` Only active workers can be assigned (this worker is ${worker.status}).` : ""}
              </span>
            )}
            {canAssign && (
              <span className="ml-auto flex flex-wrap gap-2">
                {current ? (
                  <>
                    <Button size="sm" variant="secondary" iconLeft={<ArrowRightLeft size={12} />} onClick={() => setTransferring(true)} data-testid="worker-transfer">Transfer</Button>
                    <Button size="sm" variant="ghost" iconLeft={<LogOut size={12} />} onClick={() => setEnding(true)} data-testid="worker-end-assignment">End</Button>
                  </>
                ) : worker.status === "Active" ? (
                  <Button size="sm" variant="primary" iconLeft={<UserPlus size={12} />} onClick={() => setAssigning(true)} data-testid="worker-assign">Assign workstation</Button>
                ) : null}
              </span>
            )}
          </div>
          <AssignmentHistoryTable rows={history.data ?? []} testId="worker-assignment-history" />
        </>
      )}
      <AssignDialog open={assigning} factoryId={worker.factoryId} worker={worker} onClose={() => setAssigning(false)} onDone={() => { setAssigning(false); done(); }} />
      {current && <TransferDialog open={transferring} assignment={current} onClose={() => setTransferring(false)} onDone={() => { setTransferring(false); done(); }} />}
      {current && <EndAssignmentDialog open={ending} assignment={current} onClose={() => setEnding(false)} onDone={() => { setEnding(false); done(); }} />}
    </Panel>
  );
}
