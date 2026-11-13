import Link from "next/link";
import { notFound } from "next/navigation";
import { AlertTriangle } from "lucide-react";
import { Prose, Toc } from "@/components/site/Prose";
import { Breadcrumbs, Container } from "@/components/site/ui";
import { formatDate } from "@/content/site/articles";
import { LEGAL, legalBySlug } from "@/content/site/legal";
import { pageMeta } from "@/lib/site/seo";
import { cn } from "@/lib/cn";

export function generateStaticParams() {
  return LEGAL.map((l) => ({ slug: l.slug }));
}

export async function generateMetadata({ params }: { params: Promise<{ slug: string }> }) {
  const l = legalBySlug((await params).slug);
  if (!l) return {};
  return pageMeta({ title: l.title, description: l.summary, path: `/legal/${l.slug}` });
}

export default async function LegalPage({ params }: { params: Promise<{ slug: string }> }) {
  const l = legalBySlug((await params).slug);
  if (!l) notFound();
  return (
    <div className="border-t border-brand-border">
      <Container wide className="grid gap-10 py-10 lg:grid-cols-[240px_minmax(0,1fr)_200px] lg:py-14">
        <nav aria-label="Policies" className="lg:sticky lg:top-24 lg:self-start">
          <p className="font-plexmono text-[0.7rem] uppercase tracking-[0.14em] text-brand-muted">Legal</p>
          <ul className="mt-3 space-y-0.5 text-[0.92rem]">
            {LEGAL.map((x) => (
              <li key={x.slug}>
                <Link
                  href={`/legal/${x.slug}`}
                  aria-current={x.slug === l.slug ? "page" : undefined}
                  className={cn("block rounded-md px-3 py-2", x.slug === l.slug ? "bg-brand-emerald-light/60 font-medium text-brand-text" : "text-brand-body hover:bg-brand-offwhite")}
                >
                  {x.title}
                </Link>
              </li>
            ))}
          </ul>
        </nav>
        <article className="min-w-0">
          <Breadcrumbs items={[{ label: "Legal", href: "/legal" }, { label: l.title }]} className="mb-6" />
          <h1 className="font-heading text-[2.2rem] font-semibold tracking-[-0.02em] text-brand-text md:text-[2.6rem]">{l.title}</h1>
          <p className="mt-3 text-[0.92rem] text-brand-muted">
            Last updated <time dateTime={l.updated}>{formatDate(l.updated)}</time>
          </p>
          {l.status === "draft" && (
            <div role="note" className="mt-6 flex gap-3 rounded-xl border border-brand-warning/40 bg-brand-warning/10 p-5 text-[0.95rem] text-brand-body">
              <AlertTriangle size={18} aria-hidden className="mt-0.5 shrink-0 text-[rgb(var(--brand-warning-ink))]" />
              <p>
                <strong className="text-brand-text">Draft, pending legal review.</strong> This document describes how the BunonBrain product works
                today. <span className="site-tbc">Highlighted text</span> must still be confirmed by the company or its legal advisers.
              </p>
            </div>
          )}
          <Prose blocks={l.body} className="mt-8 max-w-[740px]" />
        </article>
        <aside className="hidden lg:block">
          <div className="sticky top-24">
            <Toc blocks={l.body} />
          </div>
        </aside>
      </Container>
    </div>
  );
}
