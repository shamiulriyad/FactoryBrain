import { ButtonLink, Container, CtaBand, EmptyState, PageHero, Section } from "@/components/site/ui";
import { NEWS } from "@/content/site/articles";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "News & announcements",
  description: "Company and product announcements from BunonBrain.",
  path: "/news",
});

export default function NewsPage() {
  return (
    <>
      <PageHero
        crumbs={[{ label: "Company", href: "/company" }, { label: "News & announcements" }]}
        eyebrow="News & announcements"
        title="What's new at BunonBrain."
        lead="Product releases and company announcements. For the full product history, see our story."
        actions={
          <ButtonLink href="/about/story" variant="secondary" arrow>
            Product milestones
          </ButtonLink>
        }
      />
      <Section>
        <Container wide>
          {NEWS.length === 0 ? (
            <EmptyState icon="bell" title="No announcements yet" body="Official announcements will be published here. Product milestones to date are listed on our story page." />
          ) : (
            <ul className="divide-y divide-brand-border rounded-2xl border border-brand-border">
              {NEWS.map((n) => (
                <li key={n.title} className="p-6">
                  <time className="font-plexmono text-[0.75rem] text-brand-muted">{n.date}</time>
                  <p className="mt-1 font-heading text-[1.2rem] font-semibold text-brand-text">{n.title}</p>
                  <p className="mt-1 text-brand-body">{n.summary}</p>
                </li>
              ))}
            </ul>
          )}
        </Container>
      </Section>
      <CtaBand />
    </>
  );
}
