import { ButtonLink, Container, CtaBand, EmptyState, PageHero, Section } from "@/components/site/ui";
import { CASE_STUDIES } from "@/content/site/articles";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Case studies",
  description: "Customer case studies from factories using BunonBrain.",
  path: "/case-studies",
});

export default function CaseStudiesPage() {
  return (
    <>
      <PageHero
        crumbs={[{ label: "Resources", href: "/resources" }, { label: "Case studies" }]}
        eyebrow="Case studies"
        title="Results from real factories."
        lead="We publish case studies only with the factory's permission and with figures they have verified."
      />
      <Section>
        <Container wide>
          {CASE_STUDIES.length === 0 ? (
            <EmptyState
              icon="factory"
              title="No case studies published yet"
              body="Customer stories will appear here once factories have approved them. Until then, see the product on a simulated plant."
              action={
                <ButtonLink href="/demo" arrow>
                  Request a demo
                </ButtonLink>
              }
            />
          ) : null}
        </Container>
      </Section>
      <CtaBand />
    </>
  );
}
