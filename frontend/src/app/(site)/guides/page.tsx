import { ArticleCard } from "@/components/site/ArticleCard";
import { Card, Container, CtaBand, PageHero, Section, SectionHeader } from "@/components/site/ui";
import { ARTICLES } from "@/content/site/articles";
import { DOCS } from "@/content/site/docs";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Guides & documentation",
  description: "Practical guides for garment plants and step-by-step documentation for every BunonBrain module.",
  path: "/guides",
});

export default function GuidesPage() {
  const guides = ARTICLES.filter((a) => a.category === "Guides");
  return (
    <>
      <PageHero
        crumbs={[{ label: "Resources", href: "/resources" }, { label: "Guides" }]}
        eyebrow="Guides & documentation"
        title="Practical help, from the floor to the product."
        lead="Guides on running a better line and rolling out digital tools, plus how-to documentation for BunonBrain."
      />
      <Section label="g-title">
        <Container wide>
          <SectionHeader id="g-title" eyebrow="Guides" title="Floor practice" />
          <div className="mt-10 grid gap-5 md:grid-cols-2">
            {guides.map((a) => (
              <ArticleCard key={a.slug} a={a} />
            ))}
          </div>
        </Container>
      </Section>
      <Section tone="off" label="d-title">
        <Container wide>
          <SectionHeader id="d-title" eyebrow="Documentation" title="Using BunonBrain" />
          <div className="mt-10 grid gap-4 md:grid-cols-2 lg:grid-cols-3">
            {DOCS.map((d) => (
              <Card key={d.slug} href={`/docs/${d.slug}`} className="p-6">
                <p className="font-plexmono text-[0.68rem] uppercase tracking-[0.14em] text-brand-emerald-ink">{d.section}</p>
                <p className="mt-2 font-heading text-[1.1rem] font-semibold text-brand-text">{d.title}</p>
                <p className="mt-1.5 text-[0.93rem] leading-relaxed text-brand-body">{d.summary}</p>
              </Card>
            ))}
          </div>
        </Container>
      </Section>
      <CtaBand />
    </>
  );
}
