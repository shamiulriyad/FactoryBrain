import { ArrowRight } from "lucide-react";
import { ButtonLink, Card, Container, CtaBand, IconTile, PageHero, Section, SectionHeader, StatusBadge } from "@/components/site/ui";
import { PlatformDiagram, ScreenFrame } from "@/components/site/visuals";
import { MODULES, type ProductModule } from "@/content/site/modules";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Product overview",
  description: "The BunonBrain platform: production planning, line board and alerts, quality inspection, merchandising, approvals, Ask BunonBrain, agents, reports, access control and workforce.",
  path: "/product",
});

const GROUPS: ProductModule["group"][] = ["Production", "Operations", "Intelligence", "Workforce", "Platform"];

export default function ProductPage() {
  return (
    <>
      <PageHero
        crumbs={[{ label: "Product" }]}
        eyebrow="Product overview"
        title="Twelve modules. One record of how the plant runs."
        lead="BunonBrain is a single web application with role-based access. Modules share the same factories, lines, orders and people, so information entered once is available wherever it is needed."
        actions={
          <>
            <ButtonLink href="/demo" size="lg" arrow>
              Request a demo
            </ButtonLink>
            <ButtonLink href="/product/features" size="lg" variant="secondary">
              All features
            </ButtonLink>
          </>
        }
        aside={<ScreenFrame src="/site/screens/overview.jpg" alt="BunonBrain floor overview on simulated data" priority />}
      />

      <Section label="arch-title">
        <Container wide>
          <SectionHeader id="arch-title" eyebrow="Platform" title="How the pieces fit" lead="Factory data comes in on the left, the modules work on it under one access model, and each role sees what it is responsible for." />
          <div className="mt-12 rounded-3xl border border-brand-border bg-brand-offwhite p-4 sm:p-8">
            <PlatformDiagram />
          </div>
        </Container>
      </Section>

      {GROUPS.map((g, i) => {
        const mods = MODULES.filter((m) => m.group === g);
        return (
          <Section key={g} tone={i % 2 === 0 ? "off" : "white"} label={`g-${g}`} className="py-16 md:py-20">
            <Container wide>
              <h2 id={`g-${g}`} className="font-heading text-[1.7rem] font-semibold tracking-[-0.01em] text-brand-text">
                {g}
              </h2>
              <div className="mt-8 grid gap-5 md:grid-cols-2 lg:grid-cols-3">
                {mods.map((m) => (
                  <Card key={m.slug} href={`/product/${m.slug}`} className="flex flex-col">
                    <div className="flex items-start justify-between gap-3">
                      <IconTile name={m.icon} />
                      <StatusBadge status={m.status} />
                    </div>
                    <h3 className="mt-5 font-heading text-[1.2rem] font-semibold text-brand-text">{m.name}</h3>
                    <p className="mt-2 flex-1 leading-relaxed text-brand-body">{m.short}</p>
                    <span className="mt-6 inline-flex items-center gap-1 text-[0.9rem] font-medium text-brand-emerald-ink">
                      Learn more <ArrowRight size={14} aria-hidden />
                    </span>
                  </Card>
                ))}
              </div>
            </Container>
          </Section>
        );
      })}
      <CtaBand />
    </>
  );
}
