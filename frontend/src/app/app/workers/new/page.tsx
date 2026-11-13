"use client";

// Add worker. HR and administrators register a worker in one of their
// factories; the API generates the permanent Worker ID (W-001, …) and
// creates no login account.

import { useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Field, Select } from "@/components/ui/Field";
import { Callout } from "@/components/ui/Callout";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { WorkerForm } from "@/components/workers/WorkerForm";
import { useMyFactories } from "@/components/workers/shared";
import { apiJson } from "@/lib/api";
import { P } from "@/lib/access";
import { EMPTY_WORKER_INPUT, workerBody, type Worker, type WorkerInput } from "@/lib/workers";

export default function NewWorkerPage() {
  return (
    <RequirePermission perms={[P.WorkersManage]} what="Registering workers">
      <NewWorker />
    </RequirePermission>
  );
}

function NewWorker() {
  const router = useRouter();
  const toast = useToast();
  const { factories, error } = useMyFactories();
  const [picked, setPicked] = useState("");
  const factoryId = picked || (factories?.length === 1 ? factories[0].id : "");
  const initial = useMemo(() => ({ ...EMPTY_WORKER_INPUT }), []);

  async function create(input: WorkerInput) {
    const w = await apiJson<Worker>("/api/workers", { method: "POST", body: workerBody(input, factoryId) });
    toast.push({ tone: "success", title: `${w.workerCode} registered`, description: `${w.fullName} was added to the directory.` });
    router.push(`/app/workers/${w.id}`);
  }

  return (
    <div className="page page-narrow" data-testid="worker-new-page">
      <PageHeader eyebrow="Workforce" title="Add worker" description="Register a worker. The Worker ID is generated automatically and is never reused." />
      <div className="mt-6">
        {factories === null && !error ? (
          <SkeletonRows rows={4} rowHeight="h-16" />
        ) : error ? (
          <Callout tone="danger" role="alert">{error}</Callout>
        ) : factories!.length === 0 ? (
          <Callout tone="warning" title="No factory">You are not assigned to an active factory, so you can&apos;t register workers.</Callout>
        ) : (
          <WorkerForm
            factoryId={factoryId}
            initial={initial}
            mode="create"
            submitLabel="Register worker"
            onSubmit={create}
            onCancel={() => router.push("/app/workers")}
            header={
              factories!.length > 1 ? (
                <Panel title="Factory">
                  <Field label="Register in factory" hint="The factory can't be changed later.">
                    {({ id, describedBy }) => (
                      <Select id={id} aria-describedby={describedBy} value={factoryId} onChange={(e) => setPicked(e.target.value)} data-testid="worker-factory">
                        <option value="">Choose a factory…</option>
                        {factories!.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
                      </Select>
                    )}
                  </Field>
                </Panel>
              ) : undefined
            }
          />
        )}
      </div>
    </div>
  );
}
