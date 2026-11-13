"use client";

// Building blocks for the access-management pages (users, roles,
// permissions, factories): pickers that load their options from the API and
// small display pieces. Every option list comes from the server, so a picker
// never offers a role or factory the API would refuse.

import { useEffect, useMemo, useState } from "react";
import { ChevronLeft, ChevronRight, Check } from "lucide-react";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { SkeletonRows } from "@/components/ui/Status";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { SCOPE_LABEL, type FactoryDto, type FactoryLine, type RoleDto } from "@/lib/access";

export function StatusBadge({ active, mustChangePassword }: { active: boolean; mustChangePassword?: boolean }) {
  return (
    <span className="inline-flex flex-wrap gap-1">
      <Badge tone={active ? "success" : "danger"} dot>{active ? "Active" : "Deactivated"}</Badge>
      {active && mustChangePassword && <Badge tone="warning" title="Must change a temporary password at next sign-in">Temp. password</Badge>}
    </span>
  );
}

export function ScopeBadge({ scope }: { scope: string }) {
  return <Badge tone={scope === "Platform" ? "danger" : scope === "Factory" ? "accent" : "neutral"}>{SCOPE_LABEL[scope] ?? scope}</Badge>;
}

export function Pagination({ page, pageSize, total, onPage }: { page: number; pageSize: number; total: number; onPage: (p: number) => void }) {
  const pages = Math.max(1, Math.ceil(total / pageSize));
  const from = total === 0 ? 0 : (page - 1) * pageSize + 1;
  const to = Math.min(total, page * pageSize);
  return (
    <div className="flex flex-wrap items-center justify-between gap-2 px-4 py-3 border-t border-border-subtle text-caption text-fg-secondary">
      <span aria-live="polite">{total === 0 ? "No results" : `${from}–${to} of ${total}`}</span>
      <div className="flex items-center gap-1">
        <Button size="sm" variant="secondary" disabled={page <= 1} onClick={() => onPage(page - 1)} aria-label="Previous page">
          <ChevronLeft size={13} />
        </Button>
        <span className="px-2 tabular-nums">{page} / {pages}</span>
        <Button size="sm" variant="secondary" disabled={page >= pages} onClick={() => onPage(page + 1)} aria-label="Next page">
          <ChevronRight size={13} />
        </Button>
      </div>
    </div>
  );
}

/**
 * Role checkboxes. Roles the caller may not grant are shown disabled (with
 * the reason), and a role the user already holds stays visible even then.
 */
export function RolePicker({ value, onChange, grantable, disabled }: {
  value: string[];
  onChange: (roles: string[]) => void;
  /** Role ids the caller may grant; null = take from GET /api/roles (canGrant). */
  grantable?: string[] | null;
  disabled?: boolean;
}) {
  const roles = useResource<RoleDto[]>("/api/roles");
  if (roles.loading) return <SkeletonRows rows={4} rowHeight="h-12" />;
  if (roles.error) return <p className="text-caption text-[var(--risk-high)]">{errorMessage(roles.error)}</p>;
  const allowed = new Set(grantable ?? (roles.data ?? []).filter((r) => r.canGrant).map((r) => r.id));
  const shown = (roles.data ?? []).filter((r) => allowed.has(r.id) || value.includes(r.id));
  return (
    <fieldset className="grid gap-2 sm:grid-cols-2" disabled={disabled} data-testid="role-picker">
      <legend className="sr-only">Roles</legend>
      {shown.map((r) => {
        const checked = value.includes(r.id);
        const locked = !allowed.has(r.id);
        return (
          <label
            key={r.id}
            className={cn(
              "surface-2 p-3 flex gap-2.5 cursor-pointer border transition-colors",
              checked ? "!border-[var(--accent-border)]" : "border-transparent",
              (locked || disabled) && "opacity-60 cursor-not-allowed"
            )}
            title={locked ? "You are not allowed to grant or remove this role." : undefined}
          >
            <input
              type="checkbox"
              className="mt-0.5 accent-[var(--accent)]"
              checked={checked}
              disabled={locked || disabled}
              onChange={(e) => onChange(e.target.checked ? [...value, r.id] : value.filter((x) => x !== r.id))}
              data-testid={`role-option-${r.id}`}
            />
            <span className="min-w-0">
              <span className="flex flex-wrap items-center gap-1.5">
                <span className="text-body font-medium text-fg-primary">{r.name}</span>
                <ScopeBadge scope={r.scope} />
              </span>
              <span className="mt-1 block text-caption text-fg-secondary">{r.description}</span>
              <span className="mt-1 block text-[11px] text-fg-tertiary">{r.permissions.length} permissions</span>
            </span>
          </label>
        );
      })}
      {shown.length === 0 && <p className="text-caption text-fg-tertiary">You can&apos;t grant any role.</p>}
    </fieldset>
  );
}

/** Factory checkboxes (the API lists only the caller's factories; a Super Admin sees all). */
export function FactoryPicker({ value, onChange, disabled }: { value: string[]; onChange: (ids: string[]) => void; disabled?: boolean }) {
  const factories = useResource<FactoryDto[]>("/api/factories");
  if (factories.loading) return <SkeletonRows rows={2} rowHeight="h-10" />;
  if (factories.error) return <p className="text-caption text-[var(--risk-high)]">{errorMessage(factories.error)}</p>;
  const list = (factories.data ?? []).filter((f) => f.isActive || value.includes(f.id));
  if (list.length === 0) return <p className="text-caption text-fg-tertiary">No factories yet. A Super Admin creates them under Factories &amp; lines.</p>;
  return (
    <div className="flex flex-wrap gap-2" data-testid="factory-picker">
      {list.map((f) => {
        const on = value.includes(f.id);
        return (
          <button
            type="button"
            key={f.id}
            disabled={disabled}
            aria-pressed={on}
            onClick={() => onChange(on ? value.filter((x) => x !== f.id) : [...value, f.id])}
            className={cn(
              "inline-flex items-center gap-1.5 h-8 px-3 rounded-[var(--radius-control)] border text-caption transition-colors",
              on ? "border-[var(--accent-border)] bg-[var(--accent-soft)] text-[var(--accent)]" : "border-border-subtle text-fg-secondary hover:text-fg-primary",
              disabled && "opacity-60"
            )}
            data-testid={`factory-option-${f.code}`}
          >
            {on && <Check size={12} aria-hidden />}
            <span className="font-mono">{f.code}</span> {f.name}
            {!f.isActive && <Badge tone="danger">inactive</Badge>}
          </button>
        );
      })}
    </div>
  );
}

/** Production lines of the chosen factories (for Production Supervisors). */
export function LinePicker({ factoryIds, value, onChange, disabled }: {
  factoryIds: string[];
  value: string[];
  onChange: (ids: string[]) => void;
  disabled?: boolean;
}) {
  const [lines, setLines] = useState<FactoryLine[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const key = useMemo(() => [...factoryIds].sort().join(","), [factoryIds]);

  useEffect(() => {
    let alive = true;
    if (!key) {
      setLines([]);
      return;
    }
    setLines(null);
    Promise.all(key.split(",").map((id) => apiJson<FactoryLine[]>(`/api/factories/${encodeURIComponent(id)}/lines`)))
      .then((all) => alive && (setLines(all.flat()), setError(null)))
      .catch((e) => alive && (setError(errorMessage(e)), setLines([])));
    return () => {
      alive = false;
    };
  }, [key]);

  // Drop lines that no longer belong to a chosen factory.
  useEffect(() => {
    if (!lines) return;
    const ok = new Set(lines.map((l) => l.id));
    if (value.some((v) => !ok.has(v))) onChange(value.filter((v) => ok.has(v)));
  }, [lines]); // eslint-disable-line react-hooks/exhaustive-deps

  if (!key) return <p className="text-caption text-fg-tertiary">Choose a factory first.</p>;
  if (lines === null) return <SkeletonRows rows={1} rowHeight="h-8" />;
  if (error) return <p className="text-caption text-[var(--risk-high)]">{error}</p>;
  if (lines.length === 0) return <p className="text-caption text-fg-tertiary">These factories have no production lines yet.</p>;
  return (
    <div className="flex flex-wrap gap-2" data-testid="line-picker">
      {lines.map((l) => {
        const on = value.includes(l.id);
        return (
          <button
            type="button"
            key={l.id}
            disabled={disabled}
            aria-pressed={on}
            onClick={() => onChange(on ? value.filter((x) => x !== l.id) : [...value, l.id])}
            className={cn(
              "inline-flex items-center gap-1.5 h-8 px-3 rounded-[var(--radius-control)] border text-caption transition-colors",
              on ? "border-[var(--accent-border)] bg-[var(--accent-soft)] text-[var(--accent)]" : "border-border-subtle text-fg-secondary hover:text-fg-primary",
              disabled && "opacity-60"
            )}
          >
            {on && <Check size={12} aria-hidden />}
            {l.name}
          </button>
        );
      })}
    </div>
  );
}

/** A random temporary password that satisfies the server's rules (≥ 10 chars). */
export function generateTemporaryPassword(): string {
  const alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
  const bytes = new Uint32Array(14);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => alphabet[b % alphabet.length]).join("") + "-7";
}
