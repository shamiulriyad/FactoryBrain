import { notFound } from "next/navigation";
import { Info } from "lucide-react";
import { ButtonLink, Card, Container, CtaBand, IconTile, PageHero, Section, SectionHeader, StatusBadge } from "@/components/site/ui";
import { ScreenFrame, Steps } from "@/components/site/visuals";
import { MODULES, moduleBySlug } from "@/content/site/modules";
import { pageMeta } from "@/lib/site/seo";

export function generateStaticParams() {
  return MODULES.map((m) => ({ slug: m.slug }));
}

export async function generateMetadata({ params }: { params: Promise<{ slug: string }> }) {
  const m = moduleBySlug((await params).slug);
  if (!m) return {};
  return pageMeta({ title: m.name, description: m.summary, path: `/product/${m.slug}` });
}

export default async function ModulePage({ params }: { params: Promise<{ slug: string }> }) {
  const m = moduleBySlug((await params).slug);
  if (!m) notFound();
  const related = m.related.map(moduleBySlug).filter((x) => x !== undefined);

  return (
    <>
      <PageHero
        crumbs={[{ label: "Product", href: "/product" }, { label: m.name }]}
        eyebrow={`${m.group} module`}
        title={m.name}
        lead={m.summary}
        actions={
          <>
            <StatusBadge status={m.status} className="mr-2" />
            <ButtonLink href="/demo" arrow>
              Request a demo
            </ButtonLink>
            <ButtonLink href="/docs" variant="secondary">
              Documentation
            </ButtonLink>
          </>
        }
        aside={
          m.screenshot ? (
            <ScreenFrame
              src={m.screenshot.src}
              alt={m.screenshot.alt}
              priority
              caption={m.status === "available" && m.slug !== "workforce" ? undefined : "Demo screen · browser-only sample data"}
            />
          ) : (
            <div className="site-cta relative overflow-hidden rounded-3xl p-10 text-white">
              <div aria-hidden className="site-weave pointer-events-none absolute inset-0" />
              <IconTile name={m.icon} tone="dark" className="relative size-14" />
              <p className="relative mt-8 font-heading text-[1.5rem] font-semibold leading-snug">{m.short}</p>
            </div>
          )
        }
      />

      {m.notes && (
        <div className="border-b border-brand-border bg-brand-offwhite">
          <Container wide className="py-5">
            {m.notes.map((n) => (
              <p key={n} className="flex gap-3 text-[0.95rem] text-brand-body">
                <Info size={18} aria-hidden className="mt-0.5 shrink-0 text-brand-info" />
                {n}
              </p>
            ))}
          </Container>
        </div>
      )}

      <Section label="problem-title">
        <Container wide className="grid gap-12 lg:grid-cols-[minmax(0,0.8fr)_minmax(0,1.2fr)]">
          <SectionHeader id="problem-title" eyebrow="The problem it solves" title="Why this module exists" />
          <p className="text-[1.15rem] leading-[1.8] text-brand-body">{m.problem}</p>
        </Container>
      </Section>

      <Section tone="off" label="how-title">
        <Container wide>
          <SectionHeader id="how-title" eyebrow="How it works" title={`${m.name}, step by step`} />
          <div className="mt-12">
            <Steps items={m.how} />
          </div>
        </Container>
      </Section>

      <Section label="cap-title">
        <Container wide>
          <SectionHeader id="cap-title" eyebrow="Key capabilities" title="What it does" />
          <div className="mt-12 grid gap-5 md:grid-cols-2">
            {m.capabilities.map((c) => (
              <div key={c.title} className="rounded-2xl border border-brand-border p-6">
                <div className="flex flex-wrap items-center gap-3">
                  <h3 className="font-heading text-[1.15rem] font-semibold text-brand-text">{c.title}</h3>
                  {c.status && c.status !== "available" && <StatusBadge status={c.status} />}
                </div>
                <p className="mt-2 leading-relaxed text-brand-body">{c.body}</p>
              </div>
            ))}
          </div>
        </Container>
      </Section>

      <Section tone="navy" label="who-title">
        <Container wide>
          <h2 id="who-title" className="font-heading text-[2rem] font-semibold tracking-[-0.02em]">
            Who benefits
          </h2>
          <div className="mt-10 grid gap-5 md:grid-cols-3">
            {m.beneficiaries.map((b) => (
              <div key={b.role} className="rounded-2xl border border-white/10 bg-white/[0.04] p-6">
                <p className="font-plexmono text-[0.72rem] uppercase tracking-[0.14em] text-[rgb(var(--brand-emerald-on-dark))]">{b.role}</p>
                <p className="mt-3 leading-relaxed text-white/85">{b.benefit}</p>
              </div>
            ))}
          </div>
        </Container>
      </Section>

      {related.length > 0 && (
        <Section label="rel-title">
          <Container wide>
            <h2 id="rel-title" className="font-heading text-[1.5rem] font-semibold text-brand-text">
              Related modules
            </h2>
            <div className="mt-8 grid gap-5 md:grid-cols-3">
              {related.map((r) => (
                <Card key={r.slug} href={`/product/${r.slug}`} className="flex items-start gap-4">
                  <IconTile name={r.icon} />
                  <div>
                    <p className="font-heading text-[1.08rem] font-semibold text-brand-text">{r.name}</p>
                    <p className="mt-1 text-[0.93rem] leading-relaxed text-brand-body">{r.short}</p>
                  </div>
                </Card>
              ))}
            </div>
          </Container>
        </Section>
      )}
      <CtaBand />
    </>
  );
}
