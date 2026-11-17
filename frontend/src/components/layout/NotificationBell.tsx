"use client";

// The signed-in user's notifications (pending approvals, missing documents,
// generated drafts, decisions). Only people who must act are notified, so the
// list stays short; it refreshes every minute while the tab is visible.

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { Bell } from "lucide-react";
import { useCan } from "@/lib/auth";
import { P } from "@/lib/access";
import { useResource } from "@/lib/useResource";
import { notificationsApi, type AppNotification } from "@/lib/documents";
import { cn } from "@/lib/cn";

function ago(iso: string): string {
  const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (s < 60) return "just now";
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  return `${Math.floor(s / 86400)} d ago`;
}

export function NotificationBell() {
  const allowed = useCan(P.NotificationsView);
  const res = useResource<{ items: AppNotification[]; unread: number }>(allowed ? notificationsApi.list() : null, { pollMs: 60_000 });
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false); };
    const esc = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    document.addEventListener("mousedown", close);
    document.addEventListener("keydown", esc);
    return () => { document.removeEventListener("mousedown", close); document.removeEventListener("keydown", esc); };
  }, [open]);

  if (!allowed) return null;
  const unread = res.data?.unread ?? 0;

  async function markAll() {
    await notificationsApi.markRead();
    await res.reload();
  }

  async function markOne(n: AppNotification) {
    if (!n.readAt) { await notificationsApi.markRead([n.id]); void res.reload(); }
    setOpen(false);
  }

  return (
    <div className="relative" ref={ref}>
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-label={unread > 0 ? `Notifications, ${unread} unread` : "Notifications"}
        aria-expanded={open}
        data-testid="notification-bell"
        className="relative size-9 rounded-[var(--radius-control)] inline-flex items-center justify-center text-fg-secondary hover:text-fg-primary hover:bg-surface-2"
      >
        <Bell size={16} aria-hidden="true" />
        {unread > 0 && (
          <span className="absolute -top-0.5 -right-0.5 min-w-[18px] h-[18px] px-1 rounded-full bg-[var(--risk-high)] text-white text-[10px] font-semibold inline-flex items-center justify-center">
            {unread > 99 ? "99+" : unread}
          </span>
        )}
      </button>
      {open && (
        <div role="dialog" aria-label="Notifications" className="absolute right-0 mt-2 w-[min(92vw,380px)] surface shadow-lg z-30 overflow-hidden" data-testid="notification-panel">
          <div className="flex items-center justify-between px-3 py-2 border-b border-border-subtle">
            <span className="text-caption font-semibold">Notifications</span>
            {unread > 0 && <button type="button" className="text-caption text-accent hover:underline" onClick={() => void markAll()}>Mark all read</button>}
          </div>
          <ul className="max-h-[60vh] overflow-y-auto">
            {res.error && !res.data && <li className="px-3 py-3 text-caption text-fg-secondary">Could not load notifications.</li>}
            {res.data && res.data.items.length === 0 && <li className="px-3 py-4 text-caption text-fg-secondary">Nothing needs you right now.</li>}
            {res.data?.items.map((n) => {
              const body = (
                <>
                  <span className="block text-caption font-medium text-fg-primary">{n.title}</span>
                  <span className="block text-[12px] text-fg-secondary">{n.body}</span>
                  <span className="block text-[11px] text-fg-tertiary mt-0.5">{ago(n.createdAt)}</span>
                </>
              );
              return (
                <li key={n.id} className={cn("border-b border-border-subtle last:border-0", !n.readAt && "bg-[var(--accent-soft)]")}>
                  {n.link
                    ? <Link href={n.link} onClick={() => void markOne(n)} className="block px-3 py-2 hover:bg-surface-2">{body}</Link>
                    : <button type="button" onClick={() => void markOne(n)} className="block w-full text-left px-3 py-2 hover:bg-surface-2">{body}</button>}
                </li>
              );
            })}
          </ul>
        </div>
      )}
    </div>
  );
}
