"use client";

// Register a camera: placement (factory, floor, department, line), coverage
// and an optional health device. Video source, reference frame and zones are
// configured afterwards on the camera's page.

import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { CameraForm } from "@/components/cameras/CameraForm";
import { CameraDeviceTabs } from "@/components/cameras/shared";
import { P } from "@/lib/access";

export default function NewCameraPage() {
  return (
    <RequirePermission perms={[P.CamerasManage]} what="Camera registration">
      <div className="page" data-testid="camera-new-page">
        <PageHeader title="Register camera" description="No physical camera is needed: a recorded video or a local webcam can stand in for the prototype." />
        <CameraDeviceTabs className="mt-4" />
        <CameraForm />
      </div>
    </RequirePermission>
  );
}
