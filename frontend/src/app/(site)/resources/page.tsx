import { BookOpen, FileQuestion, LifeBuoy, Newspaper, Lightbulb, FolderOpen } from "lucide-react";
import { ArticleCard } from "@/components/site/ArticleCard";
import { ResourceExplorer } from "@/components/site/ResourceExplorer";
import { Card, Container, CtaBand, PageHero, Section, SectionHeader } from "@/components/site/ui";
import { ARTICLES } from "@/content/site/articles";
import { allResources } from "@/content/site/resources";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Resource center",
  description: "Articles, RMG industry insights, guides, product documentation and FAQs from BunonBrain.",
  path: "/resources",
});

const HUBS = [
  { icon: Newspaper, title: "Blog", href: "/blog", body: "Articles from the BunonBrain team." },
  { icon: Lightbulb, title: "RMG insights", href: "/insights", body: "Measures and practices on the garment floor." },
  { icon: BookOpen, title: "Guides", href: "/guides", body: "Step-by-step help for common tasks." },
  { icon: FolderOpen, title: "Documentation", href: "/docs", body: "How each module works." },
  { icon: FileQuestion, title: "FAQs", href: "/faq", body: "Short answers to common questions." },
  { icon: LifeBuoy, title: "Help center", href: "/help", body: "Find help or contact support." },
];

export default function ResourcesPage() {
  const featured = ARTICLES.filter((a) => a.featured);
  return (
    <>
      <PageHero
        crumbs={[{ label: "Resources" }]}
        eyebrow="Resource center"
        title="Learn how better information runs a better floor."
        lead="Explainers on the measures that matter in RMG, practical guides, and documentation for every BunonBrain module."
      />
      <Section label="feat-title" className="py-16 md:py-20">
        <Container wide>
          <h2 id="feat-title" className="font-plexmono text-[0.72rem] uppercase tracking-[0.16em] text-brand-muted">
            Featured
          </h2>
          <div className="mt-6 grid gap-5 lg:grid-cols-2">
            {featured.map((a) => (
              <ArticleCard key={a.slug} a={a} large />
            ))}
          </div>
        </Container>
      </Section>
      <Section tone="off" label="hubs-title" className="py-16 md:py-20">
        <Container wide>
          <SectionHeader id="hubs-title" eyebrow="Browse" title="Categories" />
          <div className="mt-10 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {HUBS.map((h) => (
              <Card key={h.href} href={h.href} className="flex items-start gap-4 p-6">
                <span aria-hidden className="inline-flex size-10 shrink-0 items-center justify-center rounded-lg bg-brand-navy text-white">
                  <h.icon size={18} strokeWidth={1.8} />
                </span>
                <span>
                  <span className="block font-heading text-[1.1rem] font-semibold text-brand-text">{h.title}</span>
                  <span className="mt-1 block text-[0.93rem] text-brand-body">{h.body}</span>
                </span>
              </Card>
            ))}
          </div>
        </Container>
      </Section>
      <Section label="all-title">
        <Container wide>
          <SectionHeader id="all-title" eyebrow="Search" title="All resources" />
          <div className="mt-10">
            <ResourceExplorer items={allResources()} />
          </div>
        </Container>
      </Section>
      <CtaBand title="Can't find what you need?" body="Our team can help with questions about the product or your plant." primary={{ label: "Contact support", href: "/support" }} secondary={{ label: "Request a demo", href: "/demo" }} />
    </>
  );
}
