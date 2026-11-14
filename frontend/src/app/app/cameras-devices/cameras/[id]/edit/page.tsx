"use client";

// Edit a camera's details and placement. Coverage, source, frame and zones
// are changed on the camera's page.

import { useParams } from "next/navigation";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { CameraForm } from "@/components/cameras/CameraForm";
import { CameraDeviceTabs } from "@/components/cameras/shared";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { P } from "@/lib/access";
import type { Camera } from "@/lib/cameras";

export default function EditCameraPage() {
  return (
    <RequirePermission perms={[P.CamerasManage]} what="Camera editing">
      <Edit />
    </RequirePermission>
  );
}

function Edit() {
  const { id } = useParams<{ id: string }>();
  const cam = useResource<Camera>(`/api/cameras/${id}`);
  return (
    <div className="page" data-testid="camera-edit-page">
      <PageHeader title={cam.data ? `Edit ${cam.data.code}` : "Edit camera"} description="The factory and camera ID can't change." />
      <CameraDeviceTabs className="mt-4" />
      {cam.loading ? <div className="mt-6"><SkeletonRows rows={6} /></div>
        : cam.error || !cam.data ? <ErrorState className="mt-6" title="Couldn't load the camera" body={errorMessage(cam.error)} retryLabel="Try again" onRetry={() => void cam.reload()} />
        : <CameraForm camera={cam.data} />}
    </div>
  );
}
