import { ArticleCard } from "@/components/site/ArticleCard";
import { Container, CtaBand, PageHero, Section } from "@/components/site/ui";
import { ARTICLES } from "@/content/site/articles";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "RMG industry insights",
  description: "Explainers on the measures and practices that matter in ready-made garment production: efficiency, DHU, WIP and more.",
  path: "/insights",
});

export default function InsightsPage() {
  const items = ARTICLES.filter((a) => a.category === "RMG insights");
  return (
    <>
      <PageHero
        crumbs={[{ label: "Resources", href: "/resources" }, { label: "RMG insights" }]}
        eyebrow="RMG industry insights"
        title="The numbers behind a garment floor, explained."
        lead="Clear, practical explanations of the measures supervisors and managers use every day, and the mistakes that distort them."
      />
      <Section>
        <Container wide className="grid gap-5 md:grid-cols-2 lg:grid-cols-3">
          {items.map((a) => (
            <ArticleCard key={a.slug} a={a} />
          ))}
        </Container>
      </Section>
      <CtaBand />
    </>
  );
}
