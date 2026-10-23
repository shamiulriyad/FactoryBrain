// Floor alerts store — WhatsApp-sim notifications pushed to a floor supervisor.
// Same shape as ask.store.ts / sensors.store.ts (Zustand + persist), but starts
// empty: alerts are produced by agent activity, not by the demo seed.

"use client";

import { create } from "zustand";
import { persist as storage } from "@/lib/persist";
import type { FloorAlert } from "@/data/floorAlerts";
import { FloorAlertListSchema } from "@/services/sensors.schemas";

const ALERTS_KEY = "bunonbrain:floorAlerts";

function readValidated(): FloorAlert[] {
  const raw = storage.get<unknown>(ALERTS_KEY, []);
  const parsed = FloorAlertListSchema.safeParse(raw);
  if (parsed.success) return parsed.data;
  return [];
}

type FloorAlertsState = {
  alerts: FloorAlert[];
  push: (a: FloorAlert) => void;
  markRead: (id: string) => void;
  markAllRead: () => void;
  remove: (id: string) => void;
  clear: () => void;
};

export const useFloorAlertsStore = create<FloorAlertsState>((set, get) => ({
  // Intentionally empty on first load — alerts come from real workflow events.
  alerts: readValidated(),
  push: (a) => {
    const cur = get().alerts.filter((x) => x.id !== a.id);
    const next = [a, ...cur];
    storage.set(ALERTS_KEY, next);
    set({ alerts: next });
  },
  markRead: (id) => {
    const next = get().alerts.map((a) => (a.id === id ? { ...a, read: true } : a));
    storage.set(ALERTS_KEY, next);
    set({ alerts: next });
  },
  markAllRead: () => {
    const next = get().alerts.map((a) => (a.read ? a : { ...a, read: true }));
    storage.set(ALERTS_KEY, next);
    set({ alerts: next });
  },
  remove: (id) => {
    const next = get().alerts.filter((a) => a.id !== id);
    storage.set(ALERTS_KEY, next);
    set({ alerts: next });
  },
  clear: () => {
    storage.set(ALERTS_KEY, []);
    set({ alerts: [] });
  }
}));
