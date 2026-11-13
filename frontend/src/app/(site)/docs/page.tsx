import { Card, Container, CtaBand, PageHero, Section } from "@/components/site/ui";
import { DOCS, type Doc } from "@/content/site/docs";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Product documentation",
  description: "How to use BunonBrain: getting started, roles and permissions, production planning, quality inspection, merchandising, approvals, Ask and administration.",
  path: "/docs",
});

const SECTIONS: Doc["section"][] = ["Getting started", "Modules", "Administration"];

export default function DocsIndex() {
  return (
    <>
      <PageHero crumbs={[{ label: "Resources", href: "/resources" }, { label: "Documentation" }]} eyebrow="Product documentation" title="BunonBrain documentation" lead="How each part of BunonBrain works, written for the people who use it." />
      {SECTIONS.map((s, i) => (
        <Section key={s} tone={i % 2 ? "off" : "white"} label={`ds-${i}`} className="py-14 md:py-16">
          <Container wide>
            <h2 id={`ds-${i}`} className="font-heading text-[1.6rem] font-semibold text-brand-text">
              {s}
            </h2>
            <div className="mt-8 grid gap-4 md:grid-cols-2 lg:grid-cols-3">
              {DOCS.filter((d) => d.section === s).map((d) => (
                <Card key={d.slug} href={`/docs/${d.slug}`} className="p-6">
                  <p className="font-heading text-[1.12rem] font-semibold text-brand-text">{d.title}</p>
                  <p className="mt-1.5 text-[0.93rem] leading-relaxed text-brand-body">{d.summary}</p>
                </Card>
              ))}
            </div>
          </Container>
        </Section>
      ))}
      <CtaBand title="Need hands-on help?" body="Our team can walk you through any module." primary={{ label: "Contact support", href: "/support" }} secondary={{ label: "Help center", href: "/help" }} />
    </>
  );
}
