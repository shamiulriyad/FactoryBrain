import Link from "next/link";
import { notFound } from "next/navigation";
import { Prose, Toc } from "@/components/site/Prose";
import { Breadcrumbs, Container, Eyebrow } from "@/components/site/ui";
import { DOCS, docBySlug } from "@/content/site/docs";
import { pageMeta } from "@/lib/site/seo";
import { cn } from "@/lib/cn";

export function generateStaticParams() {
  return DOCS.map((d) => ({ slug: d.slug }));
}

export async function generateMetadata({ params }: { params: Promise<{ slug: string }> }) {
  const d = docBySlug((await params).slug);
  if (!d) return {};
  return pageMeta({ title: `${d.title} · Documentation`, description: d.summary, path: `/docs/${d.slug}` });
}

export default async function DocPage({ params }: { params: Promise<{ slug: string }> }) {
  const d = docBySlug((await params).slug);
  if (!d) notFound();
  const idx = DOCS.findIndex((x) => x.slug === d.slug);
  const prev = DOCS[idx - 1];
  const next = DOCS[idx + 1];
  return (
    <div className="border-t border-brand-border">
      <Container wide className="grid gap-10 py-10 lg:grid-cols-[230px_minmax(0,1fr)_200px] lg:py-14">
        <nav aria-label="Documentation" className="lg:sticky lg:top-24 lg:self-start">
          <p className="font-plexmono text-[0.7rem] uppercase tracking-[0.14em] text-brand-muted">Documentation</p>
          <ul className="mt-3 space-y-0.5 text-[0.92rem]">
            {DOCS.map((x) => (
              <li key={x.slug}>
                <Link
                  href={`/docs/${x.slug}`}
                  aria-current={x.slug === d.slug ? "page" : undefined}
                  className={cn("block rounded-md px-3 py-2", x.slug === d.slug ? "bg-brand-emerald-light/60 font-medium text-brand-text" : "text-brand-body hover:bg-brand-offwhite")}
                >
                  {x.title}
                </Link>
              </li>
            ))}
          </ul>
        </nav>
        <article className="min-w-0">
          <Breadcrumbs items={[{ label: "Documentation", href: "/docs" }, { label: d.title }]} className="mb-6" />
          <Eyebrow>{d.section}</Eyebrow>
          <h1 className="mt-2 font-heading text-[2.2rem] font-semibold tracking-[-0.02em] text-brand-text md:text-[2.6rem]">{d.title}</h1>
          <p className="mt-3 text-[1.1rem] text-brand-body">{d.summary}</p>
          <Prose blocks={d.body} className="mt-8 max-w-[720px]" />
          <div className="mt-14 grid gap-4 border-t border-brand-border pt-8 sm:grid-cols-2">
            {prev ? (
              <Link href={`/docs/${prev.slug}`} className="rounded-xl border border-brand-border p-5 hover:border-brand-navy/30">
                <span className="text-[0.8rem] text-brand-muted">Previous</span>
                <span className="mt-1 block font-medium text-brand-text">{prev.title}</span>
              </Link>
            ) : (
              <span />
            )}
            {next && (
              <Link href={`/docs/${next.slug}`} className="rounded-xl border border-brand-border p-5 text-right hover:border-brand-navy/30">
                <span className="text-[0.8rem] text-brand-muted">Next</span>
                <span className="mt-1 block font-medium text-brand-text">{next.title}</span>
              </Link>
            )}
          </div>
          <p className="mt-8 text-[0.9rem] text-brand-muted">
            Something unclear or out of date?{" "}
            <Link href="/feedback" className="text-brand-emerald-ink underline underline-offset-2">
              Tell us
            </Link>
            .
          </p>
        </article>
        <aside className="hidden lg:block">
          <div className="sticky top-24">
            <Toc blocks={d.body} />
          </div>
        </aside>
      </Container>
    </div>
  );
}
