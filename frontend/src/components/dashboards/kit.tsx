"use client";

// Building blocks of the role dashboards: a greeting header with the factory
// picker, quick-action cards (only the ones the person may open), a plain
// section frame, and the factory-overview KPI blocks. Black and white; colour
// is kept for status only. Every number comes from the API; where there is
// nothing to show the block says so.

import Link from "next/link";
import { useMemo, type ReactNode } from "react";
import { ArrowUpRight } from "lucide-react";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { Skeleton } from "@/components/ui/Skeleton";
import { useQueryState } from "@/components/attendance/shared";
import { useMyFactories } from "@/components/workers/shared";
import { KpiCard, useDefinitions } from "@/components/analytics/shared";
import { can, displayName, useAuthStore } from "@/lib/auth";
import { roleLabel } from "@/lib/access";
import { cn } from "@/lib/cn";
import { qs } from "@/lib/monitoring";
import { useResource } from "@/lib/useResource";
import type { FactoryOverview } from "@/lib/analytics";

function greeting(now = new Date()): string {
  const hour = Number(new Intl.DateTimeFormat("en-GB", { timeZone: "Asia/Dhaka", hour: "2-digit", hour12: false }).format(now));
  return hour < 12 ? "Good morning" : hour < 17 ? "Good afternoon" : "Good evening";
}

/** The signed-in person and the factory the dashboard is about (from the URL, else their first). */
export function useHome() {
  const user = useAuthStore((s) => s.user);
  const q = useQueryState("/app");
  const { factories, error } = useMyFactories();
  const picked = q.get("factory");
  const factory = useMemo(() => factories?.find((f) => f.id === picked) ?? factories?.[0] ?? null, [factories, picked]);
  return { user, factories, factoriesError: error, factory, factoryId: factory?.id ?? "", setFactory: (id: string) => q.set({ factory: id || null }) };
}

export function Hero({ home, subtitle }: { home: ReturnType<typeof useHome>; subtitle: string }) {
  const { user, factories, factory, setFactory } = home;
  const first = (user?.fullName ?? user?.email ?? "").split(/[\s@]/)[0];
  const today = new Intl.DateTimeFormat("en-GB", { timeZone: "Asia/Dhaka", weekday: "long", day: "numeric", month: "long" }).format(new Date());
  return (
    <header className="flex flex-wrap items-end justify-between gap-4 pb-6" data-testid="home-hero">
      <div className="min-w-0">
        <p className="text-caption text-fg-tertiary">{today}{user ? ` · ${roleLabel(String(user.role))}` : ""}{factory ? ` · ${factory.name}` : ""}</p>
        <h1 className="mt-1 text-[28px] leading-9 font-semibold tracking-tight text-fg-primary" data-testid="home-greeting">{greeting()}{first ? `, ${first}` : ""}</h1>
        <p className="mt-1 max-w-2xl text-body text-fg-secondary">{subtitle}</p>
      </div>
      {factories && factories.length > 1 && (
        <FilterSelect label="Factory" value={factory?.id ?? ""} onChange={setFactory} testId="home-factory">
          {factories.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
        </FilterSelect>
      )}
    </header>
  );
}

export type Action = { href: string; icon: ReactNode; title: string; body: string; perms: string[] };

/** Big, plain links to the pages this role works in; the ones the person may not open are left out. */
export function Actions({ items, title = "Go to" }: { items: Action[]; title?: string }) {
  const user = useAuthStore((s) => s.user);
  const visible = items.filter((a) => can(user, ...a.perms));
  if (visible.length === 0) return null;
  return (
    <Section title={title}>
      <ul className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3" data-testid="home-actions">
        {visible.map((a) => (
          <li key={a.href}>
            <Link href={a.href} className="group flex h-full items-start gap-3 rounded-[var(--radius-card,12px)] border border-border-subtle bg-[var(--bg-surface)] p-4 transition-colors hover:border-border-strong hover:bg-surface-2">
              <span className="mt-0.5 inline-flex size-9 shrink-0 items-center justify-center rounded-lg bg-surface-2 text-fg-primary group-hover:bg-[var(--bg-surface)]" aria-hidden>{a.icon}</span>
              <span className="min-w-0 flex-1">
                <span className="flex items-center gap-1 font-medium text-fg-primary">{a.title}<ArrowUpRight size={14} className="text-fg-tertiary opacity-0 transition-opacity group-hover:opacity-100" aria-hidden /></span>
                <span className="mt-0.5 block text-caption text-fg-tertiary">{a.body}</span>
              </span>
            </Link>
          </li>
        ))}
      </ul>
    </Section>
  );
}

export function Section({ title, hint, href, linkLabel = "Open", className, children, testId }: {
  title: string; hint?: string; href?: string; linkLabel?: string; className?: string; children: ReactNode; testId?: string;
}) {
  return (
    <section className={cn("mb-8", className)} data-testid={testId}>
      <div className="mb-3 flex items-baseline justify-between gap-3">
        <div className="min-w-0">
          <h2 className="text-[15px] font-semibold text-fg-primary">{title}</h2>
          {hint && <p className="text-caption text-fg-tertiary">{hint}</p>}
        </div>
        {href && <Link href={href} className="shrink-0 text-caption text-fg-secondary underline-offset-4 hover:text-fg-primary hover:underline">{linkLabel}</Link>}
      </div>
      {children}
    </section>
  );
}

/** One bordered block of content (tables, lists). */
export function Card({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cn("overflow-hidden rounded-[var(--radius-card,12px)] border border-border-subtle bg-[var(--bg-surface)]", className)}>{children}</div>;
}

export function Empty({ children }: { children: ReactNode }) {
  return <p className="px-4 py-6 text-center text-caption text-fg-tertiary">{children}</p>;
}

/** The factory overview (analytics), fetched once per dashboard. */
export function useOverview(factoryId: string) {
  return useResource<FactoryOverview>(factoryId ? `/api/analytics/overview${qs({ factoryId })}` : null, { pollMs: 60_000 });
}

/** The KPI groups named in <c>ids</c>, as quiet cards; hidden groups (no permission) are left out. */
export function OverviewGroups({ overview, ids, testId }: { overview: ReturnType<typeof useOverview>; ids: string[]; testId?: string }) {
  const defs = useDefinitions();
  if (overview.error && !overview.data) return <Callout tone="warning" title="Could not load the factory figures">{overview.error.message}</Callout>;
  if (!overview.data) return <div className="grid gap-3 grid-cols-2 xl:grid-cols-4">{[0, 1, 2, 3].map((i) => <Skeleton key={i} className="h-24" />)}</div>;
  const groups = overview.data.groups.filter((g) => ids.includes(g.id)).sort((a, b) => ids.indexOf(a.id) - ids.indexOf(b.id));
  const shown = groups.map((g) => ({ ...g, kpis: g.kpis.filter((k) => k.availability !== "NOT_PERMITTED") })).filter((g) => g.kpis.length > 0);
  if (shown.length === 0) return <Callout tone="info">No figures for your role here yet.</Callout>;
  return (
    <div className="space-y-6" data-testid={testId}>
      {overview.data.warnings.length > 0 && <p className="text-caption text-fg-tertiary">{overview.data.warnings[0]}</p>}
      {shown.map((g) => (
        <div key={g.id}>
          <div className="mb-2 flex items-baseline justify-between"><h3 className="text-caption font-medium text-fg-secondary">{g.title}</h3>
            {g.link && <Link href={g.link} className="text-caption text-fg-tertiary hover:text-fg-primary hover:underline underline-offset-4">Details</Link>}</div>
          <div className="grid gap-3 grid-cols-2 md:grid-cols-3 xl:grid-cols-4">{g.kpis.map((k) => <KpiCard key={k.metricId} k={k} defs={defs} />)}</div>
        </div>
      ))}
    </div>
  );
}

export function NoFactory({ home }: { home: ReturnType<typeof useHome> }) {
  if (home.factoriesError) return <Callout tone="danger" title="Could not load your factories">{home.factoriesError}</Callout>;
  if (home.factories === null) return <div className="space-y-3"><Skeleton className="h-24" /><Skeleton className="h-24" /></div>;
  return <Callout tone="warning" title="No factory assigned">Your account isn&apos;t assigned to a factory yet, so there are no figures to show. Ask an administrator.</Callout>;
}

export { displayName };
