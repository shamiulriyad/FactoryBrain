import { dataset } from "./dataset";
import type { ActivityItemPublic } from "./types";

/** In-memory + module-level append log. Keeps the demo's seed activity
    and lets the rest of the app add entries without persisting to localStorage
    (which would cause cross-tab noise). */
const live: ActivityItemPublic[] = [];
for (const a of dataset.activity) live.push(a);

export const activityService = {
  recent(opts?: { limit?: number }): ActivityItemPublic[] {
    const items = live.slice();
    items.sort((a, b) => +new Date(b.isoDate) - +new Date(a.isoDate));
    return opts?.limit ? items.slice(0, opts.limit) : items;
  },
  count(): number {
    return live.length;
  },
  /** Push a new entry. */
  push(entry: Omit<ActivityItemPublic, "id" | "isoDate"> & { isoDate?: string }) {
    const id = `act-live-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`;
    const isoDate = entry.isoDate ?? new Date().toISOString();
    live.unshift({ ...entry, id, isoDate });
    return id;
  },
  countToday(): number {
    const today = new Date().toDateString();
    return live.filter((a) => new Date(a.isoDate).toDateString() === today).length;
  }
};