"use client";

import { useParams } from "next/navigation";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { DeviceForm } from "@/components/cameras/DeviceForm";
import { CameraDeviceTabs } from "@/components/cameras/shared";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { P } from "@/lib/access";
import type { Device } from "@/lib/cameras";

export default function EditDevicePage() {
  return (
    <RequirePermission perms={[P.DevicesManage]} what="Device editing">
      <Edit />
    </RequirePermission>
  );
}

function Edit() {
  const { id } = useParams<{ id: string }>();
  const dev = useResource<Device>(`/api/devices/${id}`);
  return (
    <div className="page" data-testid="device-edit-page">
      <PageHeader title={dev.data ? `Edit ${dev.data.code}` : "Edit device"} description="The factory, device ID and real / simulated mode can't change." />
      <CameraDeviceTabs className="mt-4" />
      {dev.loading ? <div className="mt-6"><SkeletonRows rows={6} /></div>
        : dev.error || !dev.data ? <ErrorState className="mt-6" title="Couldn't load the device" body={errorMessage(dev.error)} retryLabel="Try again" onRetry={() => void dev.reload()} />
        : <DeviceForm device={dev.data} />}
    </div>
  );
}
