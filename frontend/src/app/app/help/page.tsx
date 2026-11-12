"use client";

import Link from "next/link";
import { Compass, MessageSquareText, Search, UserRound } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { useT } from "@/lib/useT";

export default function HelpPage() {
  const { t } = useT();
  return (
    <div className="page page-narrow space-y-6">
      <PageHeader title={t("placeholder.helpTitle")} description={t("placeholder.helpBody")} />
      <ul className="divide-y divide-border-subtle rounded-lg border border-border-subtle bg-[var(--bg-surface)]">
        <li>
          <button
            type="button"
            onClick={() => window.dispatchEvent(new CustomEvent("bunonbrain:restart-tour"))}
            className="flex min-h-11 w-full items-center gap-3 px-4 py-3 text-left text-body text-fg-primary hover:text-[var(--accent)]"
          >
            <Compass size={16} aria-hidden="true" className="text-fg-tertiary" />
            {t("placeholder.restartTour")}
          </button>
        </li>
        <li className="flex min-h-11 items-center gap-3 px-4 py-3 text-body text-fg-secondary">
          <Search size={16} aria-hidden="true" className="text-fg-tertiary" />
          {t("placeholder.searchTip")}
        </li>
        <li>
          <Link href="/app/ask" className="flex min-h-11 items-center gap-3 px-4 py-3 text-body text-fg-primary hover:text-[var(--accent)]">
            <MessageSquareText size={16} aria-hidden="true" className="text-fg-tertiary" />
            {t("nav.ask")}
          </Link>
        </li>
        <li className="flex min-h-11 items-center gap-3 px-4 py-3 text-body text-fg-secondary">
          <UserRound size={16} aria-hidden="true" className="text-fg-tertiary" />
          {t("placeholder.contact")}
        </li>
      </ul>
    </div>
  );
}
