"use client";

// /app/production — production planning & line efficiency: overview, orders,
// plans, line balancing, output history and plant setup. Everything is read
// from and written to /api/production/**; plans and balance proposals become
// operational only through the approval queue plus an explicit activate /
// apply step.

import { useEffect, useState } from "react";
import { FileUp, PlusCircle } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Tabs } from "@/components/ui/Tabs";
import { canOperate, isAdmin, useAuthStore } from "@/lib/auth";
import { errorMessage } from "@/lib/api";
import { ProductionOverview } from "@/components/production/ProductionOverview";
import { HistoryPanel, OrdersPanel, PlansPanel } from "@/components/production/panels";
import { BalancingPanel } from "@/components/production/BalancingPanel";
import { SetupPanel } from "@/components/production/SetupPanel";
import { ImportOutputModal, OutputFormModal } from "@/components/production/forms";
import { useMasterData, useProductionConfig } from "@/components/production/shared";

type Tab = "overview" | "orders" | "plans" | "balancing" | "history" | "setup";
const TABS: Tab[] = ["overview", "orders", "plans", "balancing", "history", "setup"];

export default function ProductionPage() {
  const user = useAuthStore((s) => s.user);
  const operator = canOperate(user);
  const admin = isAdmin(user);
  const [tab, setTab] = useState<Tab>("overview");
  const config = useProductionConfig();
  const md = useMasterData();
  const [recording, setRecording] = useState(false);
  const [importing, setImporting] = useState(false);

  useEffect(() => {
    const t = new URLSearchParams(window.location.search).get("tab") as Tab | null;
    if (t && TABS.includes(t)) setTab(t);
  }, []);

  function changeTab(t: Tab) {
    setTab(t);
    const url = new URL(window.location.href);
    if (t === "overview") url.searchParams.delete("tab");
    else url.searchParams.set("tab", t);
    window.history.replaceState(null, "", url);
  }

  const lines = md.lines.data ?? [];
  const shifts = md.shifts.data ?? [];
  const styles = md.styles.data ?? [];

  return (
    <div className="page" data-testid="production-page">
      <PageHeader
        eyebrow="Operations"
        title="Production planning"
        description="Plans, recorded output, line efficiency, delivery risk and line balancing, calculated from this plant's own records."
        actions={operator && (
          <>
            <Button variant="secondary" iconLeft={<FileUp size={14} />} onClick={() => setImporting(true)} data-testid="output-import">Import CSV</Button>
            <Button variant="primary" iconLeft={<PlusCircle size={14} />} onClick={() => setRecording(true)} data-testid="output-record">Record output</Button>
          </>
        )}
      />
      {config.error && !config.data && (
        <Callout tone="danger" className="mt-4" title="Production data is unavailable" action={<Button size="sm" onClick={() => void config.reload()}>Retry</Button>}>
          {errorMessage(config.error)}
        </Callout>
      )}
      <Tabs<Tab>
        className="mt-5"
        label="Production views"
        value={tab}
        onChange={changeTab}
        items={[
          { id: "overview", label: "Overview", testId: "prod-tab-overview" },
          { id: "orders", label: "Orders", testId: "prod-tab-orders" },
          { id: "plans", label: "Plans", testId: "prod-tab-plans" },
          { id: "balancing", label: "Line balancing", testId: "prod-tab-balancing" },
          { id: "history", label: "Output history", testId: "prod-tab-history" },
          { id: "setup", label: "Setup", testId: "prod-tab-setup" }
        ]}
      />
      <div className="mt-4" role="tabpanel">
        {tab === "overview" && <ProductionOverview config={config.data} offset={config.offset} lines={lines} shifts={shifts} admin={admin} />}
        {tab === "orders" && <OrdersPanel operator={operator} styles={styles} />}
        {tab === "plans" && <PlansPanel operator={operator} lines={lines} shifts={shifts} />}
        {tab === "balancing" && <BalancingPanel operator={operator} lines={lines} styles={styles} />}
        {tab === "history" && <HistoryPanel operator={operator} lines={lines} shifts={shifts} offset={config.offset} />}
        {tab === "setup" && <SetupPanel config={config.data} admin={admin} />}
      </div>
      <OutputFormModal open={recording} onClose={() => setRecording(false)} lines={lines} offset={config.offset} />
      <ImportOutputModal open={importing} onClose={() => setImporting(false)} offset={config.offset} />
    </div>
  );
}
