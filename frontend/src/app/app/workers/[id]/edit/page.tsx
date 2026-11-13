"use client";

// Edit worker: the complete editable record. The Worker ID and the factory
// never change; the status is changed from the worker's page (with a reason
// and history), not here.

import Link from "next/link";
import { useMemo } from "react";
import { useParams, useRouter } from "next/navigation";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { Callout } from "@/components/ui/Callout";
import { useToast } from "@/components/ui/Toast";
import { WorkerForm } from "@/components/workers/WorkerForm";
import { WorkerStatusBadge } from "@/components/workers/shared";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage, isApiError } from "@/lib/api";
import { P } from "@/lib/access";
import { inputFromWorker, workerBody, type Worker, type WorkerInput, type WorkerProfile } from "@/lib/workers";

export default function EditWorkerPage() {
  return (
    <RequirePermission perms={[P.WorkersManage]} what="Editing workers">
      <EditWorker />
    </RequirePermission>
  );
}

function EditWorker() {
  const { id } = useParams<{ id: string }>();
  const router = useRouter();
  const toast = useToast();
  const profile = useResource<WorkerProfile>(`/api/workers/${encodeURIComponent(id)}/profile`);
  const w = profile.data?.worker;
  const initial = useMemo(() => (w ? inputFromWorker(w) : null), [w]);

  async function save(input: WorkerInput) {
    const saved = await apiJson<Worker>(`/api/workers/${w!.id}`, { method: "PUT", body: workerBody(input) });
    toast.push({ tone: "success", title: "Changes saved", description: `${saved.workerCode} · ${saved.fullName}` });
    router.push(`/app/workers/${saved.id}`);
  }

  return (
    <div className="page page-narrow" data-testid="worker-edit-page">
      <PageHeader
        eyebrow={w ? <Link href={`/app/workers/${w.id}`} className="hover:underline">← {w.workerCode}</Link> : "Workforce"}
        title={w ? `Edit ${w.fullName}` : "Edit worker"}
        description="The Worker ID and the factory can't be changed. Change the status from the worker's page."
        meta={w ? <><span className="font-mono text-caption">{w.workerCode}</span><WorkerStatusBadge status={w.status} /></> : undefined}
      />
      <div className="mt-6">
        {profile.loading ? (
          <SkeletonRows rows={5} rowHeight="h-20" />
        ) : profile.error || !profile.data ? (
          <ErrorState
            title={isApiError(profile.error) && profile.error.status === 404 ? "Worker not found" : "Couldn't load the worker"}
            body={profile.error ? errorMessage(profile.error) : undefined}
            retryLabel="Try again"
            onRetry={() => void profile.reload()}
          />
        ) : !profile.data.can.edit ? (
          <Callout tone="warning" title="Read only">You can&apos;t edit this worker.</Callout>
        ) : (
          <WorkerForm
            factoryId={profile.data.worker.factoryId}
            initial={initial!}
            mode="edit"
            submitLabel="Save changes"
            onSubmit={save}
            onCancel={() => router.push(`/app/workers/${w!.id}`)}
          />
        )}
      </div>
    </div>
  );
}
