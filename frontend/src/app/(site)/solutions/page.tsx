import { ArrowRight } from "lucide-react";
import { Card, Container, CtaBand, IconTile, PageHero, Section, SectionHeader } from "@/components/site/ui";
import { moduleBySlug } from "@/content/site/modules";
import { SOLUTIONS } from "@/content/site/solutions";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Our solutions",
  description: "BunonBrain solutions for garment factories: workforce management, factory operations, production management, quality management and analytics.",
  path: "/solutions",
});

export default function SolutionsPage() {
  return (
    <>
      <PageHero
        crumbs={[{ label: "Solutions" }]}
        eyebrow="Our solutions"
        title="Solutions for every part of the garment plant."
        lead="Each solution brings together the BunonBrain modules a function needs, so planning, the floor, quality and HR work from the same records."
      />
      <Section>
        <Container wide className="grid gap-6 md:grid-cols-2">
          {SOLUTIONS.map((s) => (
            <Card key={s.slug} href={`/solutions/${s.slug}`} className="flex flex-col p-8">
              <IconTile name={s.icon} />
              <h2 className="mt-6 font-heading text-[1.55rem] font-semibold tracking-[-0.01em] text-brand-text">{s.name}</h2>
              <p className="mt-2 text-[1.02rem] leading-relaxed text-brand-body">{s.headline}</p>
              <p className="mt-6 font-plexmono text-[0.7rem] uppercase tracking-[0.14em] text-brand-muted">Modules</p>
              <ul className="mt-2 flex flex-wrap gap-2">
                {s.modules.map((m) => (
                  <li key={m} className="rounded-full border border-brand-border bg-brand-offwhite px-3 py-1 text-[0.82rem] text-brand-body">
                    {moduleBySlug(m)?.name}
                  </li>
                ))}
              </ul>
              <span className="mt-7 inline-flex items-center gap-1 font-medium text-brand-emerald-ink">
                Explore {s.name.toLowerCase()} <ArrowRight size={15} aria-hidden className="transition-transform group-hover:translate-x-0.5" />
              </span>
            </Card>
          ))}
        </Container>
      </Section>
      <Section tone="off" label="ind-title">
        <Container wide className="flex flex-wrap items-center justify-between gap-8">
          <SectionHeader id="ind-title" eyebrow="Industries" title="Made for knit, woven and export garment houses." lead="See how BunonBrain fits the production models common in Bangladesh." />
          <a href="/industries" className="inline-flex min-h-11 items-center gap-2 rounded-lg border border-brand-border bg-white px-5 font-medium text-brand-text hover:border-brand-navy/30">
            Industries we serve <ArrowRight size={15} aria-hidden />
          </a>
        </Container>
      </Section>
      <CtaBand />
    </>
  );
}
