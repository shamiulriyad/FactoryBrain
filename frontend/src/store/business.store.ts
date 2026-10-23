"use client";

import { create } from "zustand";
import { persist as storage } from "@/lib/persist";

export type BusinessProfile = {
  businessName: string;
  industry: string;
  city: string;
  currency: string;
  timezone: string;
  fiscalMonthStart: number;
  whatYouSell: string;
  customers: string;
  goals: string[];
  /** Factory the user is running (one factory per workspace). */
  factoryName?: string;
};

export type IngestionSourceCategory = "sensor" | "pilot" | "out_of_scope";

/** Built-in source → category. Used to stamp `category` onto any IngestionSource
 *  in the store that doesn't carry it explicitly (so persisted sources from
 *  earlier versions still render in the right group). */
export const SOURCE_CATEGORY: Record<string, IngestionSourceCategory> = {
  "rfid-bundles": "sensor",
  "machine-telemetry": "sensor",
  "energy-meter": "sensor",
  sheets: "pilot",
  csv: "pilot",
  documents: "pilot",
  whatsapp: "out_of_scope",
  shopify: "out_of_scope",
  facebook: "out_of_scope",
  instagram: "out_of_scope"
};

export type IngestionSource = {
  id: string;
  connected: boolean;
  lastSync: string | null;
  objectTypes: string[];
  records: number;
  /** Sensor | pilot | out_of_scope. Inferred from SOURCE_CATEGORY for sources
   *  that don't carry the field explicitly (back-compat with persisted state). */
  category?: IngestionSourceCategory;
  /** The user-entered value (sheet URL, phone, etc.) — masked when displayed. */
  value?: string;
  /** Optional filename for CSV upload. */
  filename?: string;
};

type BusinessState = {
  onboardingComplete: boolean;
  profile: BusinessProfile;
  sources: IngestionSource[];
  /** Per-agent execution mode override (e.g., "auto" | "approval" | "paused"). */
  agentMode: Record<string, "auto" | "approval" | "paused">;
  /** Risk thresholds in BDT or %, used by Approvals/Insights to re-tier. */
  thresholds: {
    orderValueBdt: number;
    discountPct: number;
    inventorySpendBdt: number;
    messageVolume: number;
  };
  /** Which risk tiers require approval. */
  approvalGate: { low: boolean; medium: boolean; high: boolean };
  /** Auto-approve below this risk tier. */
  autoApproveBelow: "low" | "medium" | "high" | "never";
  /** Notifications: which channels are on, and the daily digest time. */
  notifications: { inApp: boolean; email: boolean; whatsapp: boolean; digestTime: string };
  /** UI density. */
  density: "comfortable" | "compact";
  /** Global pause flag. */
  globalPaused: boolean;
  /** Agent currently pre-selected from the Ask BunonBrain page. */
  selectedAgentId: string | null;
  /** Feature flags for stretch / experimental functionality. */
  featureFlags: { visionRepair: boolean; whatsappAlert: boolean };
  /** Whether the simulated-data badge is shown on sensor feeds. */
  simulatedData: boolean;
  setProfile: (p: Partial<BusinessProfile>) => void;
  completeOnboarding: () => void;
  upsertSource: (id: string, patch: Partial<IngestionSource>) => void;
  bumpSource: (id: string, records: number) => void;
  setAgentMode: (id: string, mode: "auto" | "approval" | "paused") => void;
  setThresholds: (t: Partial<BusinessState["thresholds"]>) => void;
  setApprovalGate: (g: Partial<BusinessState["approvalGate"]>) => void;
  setAutoApproveBelow: (t: BusinessState["autoApproveBelow"]) => void;
  setNotifications: (n: Partial<BusinessState["notifications"]>) => void;
  setDensity: (d: BusinessState["density"]) => void;
  setGlobalPaused: (v: boolean) => void;
  setSelectedAgent: (id: string | null) => void;
  setFeatureFlag: (key: keyof BusinessState["featureFlags"], v: boolean) => void;
  setSimulatedData: (v: boolean) => void;
  /** Returns all config as a JSON-safe blob. */
  exportConfig(): string;
  /** Replaces state from a previously-exported JSON blob. */
  importConfig(json: string): { ok: boolean; error?: string };
};

const safeGet = <T,>(key: string, fallback: T): T => storage.get<T>(key, fallback);

const defaultProfile: BusinessProfile = {
  businessName: "",
  industry: "",
  city: "Dhaka",
  currency: "BDT",
  timezone: "Asia/Dhaka",
  fiscalMonthStart: 1,
  whatYouSell: "",
  customers: "",
  goals: []
};

const defaultSources: IngestionSource[] = [
  // Sensors first — kept as the headline sources for the RMG pilot. Calibration
  // notes surface in the Integrations UI alongside the simulated pill.
  { id: "rfid-bundles", category: "sensor", connected: false, lastSync: null, objectTypes: ["orders"], records: 0 },
  { id: "machine-telemetry", category: "sensor", connected: false, lastSync: null, objectTypes: ["inventory"], records: 0 },
  { id: "energy-meter", category: "sensor", connected: false, lastSync: null, objectTypes: ["inventory"], records: 0 },
  // Pilot sources — kept, but not on the same headline row as the sensors.
  { id: "documents", category: "pilot", connected: false, lastSync: null, objectTypes: ["policies", "suppliers"], records: 0 },
  { id: "csv", category: "pilot", connected: false, lastSync: null, objectTypes: ["products", "orders", "customers", "inventory"], records: 0 },
  { id: "sheets", category: "pilot", connected: false, lastSync: null, objectTypes: ["products", "orders"], records: 0 },
  // Out-of-scope for the RMG pilot — kept in the data model so existing
  // connected sources stay queryable, but moved to a disclosed/demoted group.
  { id: "shopify", category: "out_of_scope", connected: false, lastSync: null, objectTypes: ["products", "orders", "customers"], records: 0 },
  { id: "whatsapp", category: "out_of_scope", connected: false, lastSync: null, objectTypes: ["conversations"], records: 0 },
  { id: "facebook", category: "out_of_scope", connected: false, lastSync: null, objectTypes: ["orders", "customers"], records: 0 },
  { id: "instagram", category: "out_of_scope", connected: false, lastSync: null, objectTypes: ["orders", "customers"], records: 0 }
];

export const useBusinessStore = create<BusinessState>((set, get) => ({
  onboardingComplete: safeGet<boolean>("bunonbrain:onboardingComplete", false),
  profile: safeGet<BusinessProfile>("bunonbrain:profile", defaultProfile),
  sources: safeGet<IngestionSource[]>("bunonbrain:sources", defaultSources),
  agentMode: safeGet<Record<string, "auto" | "approval" | "paused">>("bunonbrain:agentMode", {}),
  thresholds: safeGet<BusinessState["thresholds"]>("bunonbrain:thresholds", {
    orderValueBdt: 25000,
    discountPct: 10,
    inventorySpendBdt: 25000,
    messageVolume: 50
  }),
  approvalGate: safeGet<BusinessState["approvalGate"]>("bunonbrain:approvalGate", {
    low: false,
    medium: true,
    high: true
  }),
  autoApproveBelow: safeGet<BusinessState["autoApproveBelow"]>("bunonbrain:autoApproveBelow", "low"),
  notifications: safeGet<BusinessState["notifications"]>("bunonbrain:notifications", {
    inApp: true,
    email: false,
    whatsapp: false,
    digestTime: "08:00"
  }),
  density: safeGet<BusinessState["density"]>("bunonbrain:density", "comfortable"),
  globalPaused: safeGet<boolean>("bunonbrain:globalPaused", false),
  selectedAgentId: safeGet<string | null>("bunonbrain:selectedAgentId", null),
  featureFlags: safeGet<BusinessState["featureFlags"]>("bunonbrain:featureFlags", {
    // On by default so the JUDGES.md Vision walkthrough is reachable without Settings.
    visionRepair: true,
    whatsappAlert: true
  }),
  simulatedData: safeGet<boolean>("bunonbrain:simulatedData", true),
  setProfile: (p) => {
    const next = { ...get().profile, ...p };
    storage.set("bunonbrain:profile", next);
    set({ profile: next });
  },
  completeOnboarding: () => {
    storage.set("bunonbrain:onboardingComplete", true);
    set({ onboardingComplete: true });
  },
  upsertSource: (id, patch) => {
    const next = get().sources.map((s) => (s.id === id ? { ...s, ...patch } : s));
    storage.set("bunonbrain:sources", next);
    set({ sources: next });
  },
  bumpSource: (id, records) => {
    const next = get().sources.map((s) => (s.id === id ? { ...s, records: Math.max(0, s.records + records) } : s));
    storage.set("bunonbrain:sources", next);
    set({ sources: next });
  },
  setAgentMode: (id, mode) => {
    const next = { ...get().agentMode, [id]: mode };
    storage.set("bunonbrain:agentMode", next);
    set({ agentMode: next });
  },
  setThresholds: (t) => {
    const next = { ...get().thresholds, ...t };
    storage.set("bunonbrain:thresholds", next);
    set({ thresholds: next });
  },
  setApprovalGate: (g) => {
    const next = { ...get().approvalGate, ...g };
    storage.set("bunonbrain:approvalGate", next);
    set({ approvalGate: next });
  },
  setAutoApproveBelow: (v) => {
    storage.set("bunonbrain:autoApproveBelow", v);
    set({ autoApproveBelow: v });
  },
  setNotifications: (n) => {
    const next = { ...get().notifications, ...n };
    storage.set("bunonbrain:notifications", next);
    set({ notifications: next });
  },
  setDensity: (d) => {
    storage.set("bunonbrain:density", d);
    set({ density: d });
  },
  setGlobalPaused: (v) => {
    storage.set("bunonbrain:globalPaused", v);
    set({ globalPaused: v });
  },
  setSelectedAgent: (id) => {
    storage.set("bunonbrain:selectedAgentId", id);
    set({ selectedAgentId: id });
  },
  setFeatureFlag: (key, v) => {
    const cur = get().featureFlags;
    const next = { ...cur, [key]: v };
    storage.set("bunonbrain:featureFlags", next);
    set({ featureFlags: next });
  },
  setSimulatedData: (v) => {
    storage.set("bunonbrain:simulatedData", v);
    set({ simulatedData: v });
  },
  exportConfig() {
    const s = get();
    return JSON.stringify(
      {
        profile: s.profile,
        sources: s.sources,
        agentMode: s.agentMode,
        thresholds: s.thresholds,
        approvalGate: s.approvalGate,
        autoApproveBelow: s.autoApproveBelow,
        notifications: s.notifications,
        density: s.density
      },
      null,
      2
    );
  },
  importConfig(json) {
    try {
      const parsed = JSON.parse(json);
      const next: Partial<BusinessState> = {};
      if (parsed.profile) next.profile = { ...get().profile, ...parsed.profile };
      if (parsed.sources) next.sources = parsed.sources;
      if (parsed.agentMode) next.agentMode = parsed.agentMode;
      if (parsed.thresholds) next.thresholds = parsed.thresholds;
      if (parsed.approvalGate) next.approvalGate = parsed.approvalGate;
      if (parsed.autoApproveBelow) next.autoApproveBelow = parsed.autoApproveBelow;
      if (parsed.notifications) next.notifications = parsed.notifications;
      if (parsed.density) next.density = parsed.density;
      // Persist each
      const cur = get();
      const merged = { ...cur, ...next } as BusinessState;
      if (next.profile) storage.set("bunonbrain:profile", merged.profile);
      if (next.sources) storage.set("bunonbrain:sources", merged.sources);
      if (next.agentMode) storage.set("bunonbrain:agentMode", merged.agentMode);
      if (next.thresholds) storage.set("bunonbrain:thresholds", merged.thresholds);
      if (next.approvalGate) storage.set("bunonbrain:approvalGate", merged.approvalGate);
      if (next.autoApproveBelow) storage.set("bunonbrain:autoApproveBelow", merged.autoApproveBelow);
      if (next.notifications) storage.set("bunonbrain:notifications", merged.notifications);
      if (next.density) storage.set("bunonbrain:density", merged.density);
      set(merged);
      return { ok: true };
    } catch (e) {
      return { ok: false, error: String(e) };
    }
  }
}));

/** Returns how many sources are connected — used by Overview nudge. */
export function connectedSourceCount(): number {
  return useBusinessStore.getState().sources.filter((s) => s.connected).length;
}
