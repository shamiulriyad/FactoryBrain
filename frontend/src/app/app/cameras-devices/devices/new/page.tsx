"use client";

// Register a device: no physical hardware is needed. Choose simulated for
// demonstrations (driven by the device simulator) or real for hardware that
// will send heartbeats.

import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { DeviceForm } from "@/components/cameras/DeviceForm";
import { CameraDeviceTabs } from "@/components/cameras/shared";
import { P } from "@/lib/access";

export default function NewDevicePage() {
  return (
    <RequirePermission perms={[P.DevicesManage]} what="Device registration">
      <div className="page" data-testid="device-new-page">
        <PageHeader title="Register device" description="RFID readers, machine sensors, production counters, ESP32 / industrial gateways and camera health devices." />
        <CameraDeviceTabs className="mt-4" />
        <DeviceForm />
      </div>
    </RequirePermission>
  );
}
