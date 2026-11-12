"use client";

import Link from "next/link";
import type { ReactNode } from "react";
import { ArrowRight } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { ALL_NAV_ITEMS } from "@/lib/navigation";
import { useT } from "@/lib/useT";

/**
 * Placeholder for a page that has its place in the navigation but isn't
 * built yet. Says so plainly and links to the pages that cover the same
 * ground today.
 */
export function ComingSoon({
  title,
  description,
  eyebrow,
  meta,
  related,
}: {
  title: ReactNode;
  description: string;
  eyebrow?: ReactNode;
  meta?: ReactNode;
  /** Nav item ids from lib/navigation (e.g. "production", "alerts"). */
  related: string[];
}) {
  const { t } = useT();
  const links = related
    .map((id) => ALL_NAV_ITEMS.find((it) => it.id === id))
    .filter((it): it is NonNullable<typeof it> => Boolean(it));

  return (
    <div className="page page-narrow space-y-6">
      <PageHeader eyebrow={eyebrow} title={title} description={description} meta={meta} />
      <section className="rounded-lg border border-border-subtle bg-[var(--bg-surface)] p-5" aria-labelledby="coming-soon-title">
        <h2 id="coming-soon-title" className="text-title text-fg-primary">
          {t("placeholder.notBuilt")}
        </h2>
        <p className="mt-1.5 text-body text-fg-secondary">{t("placeholder.notBuiltBody")}</p>
        {links.length > 0 && (
          <ul className="mt-4 divide-y divide-border-subtle border-t border-border-subtle">
            {links.map((it) => {
              const Icon = it.icon;
              return (
                <li key={it.id}>
                  <Link
                    href={it.href}
                    className="flex min-h-11 items-center gap-3 py-2 text-body text-fg-primary hover:text-[var(--accent)]"
                  >
                    {Icon && <Icon size={16} aria-hidden="true" className="text-fg-tertiary" />}
                    <span className="flex-1">{t(it.labelKey)}</span>
                    <ArrowRight size={14} aria-hidden="true" className="text-fg-tertiary" />
                  </Link>
                </li>
              );
            })}
          </ul>
        )}
      </section>
    </div>
  );
}
