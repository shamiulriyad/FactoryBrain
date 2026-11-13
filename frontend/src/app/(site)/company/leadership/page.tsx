import { ButtonLink, Container, CtaBand, EmptyState, PageHero, Section } from "@/components/site/ui";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Leadership & team",
  description: "The leadership and team behind BunonBrain.",
  path: "/company/leadership",
});

/**
 * Leadership profiles: add verified entries here (name, role, short bio,
 * optional photo under /public/site/team). The empty state shows until then.
 */
const LEADERS: { name: string; role: string; bio: string }[] = [];

export default function LeadershipPage() {
  return (
    <>
      <PageHero
        crumbs={[{ label: "Company", href: "/company" }, { label: "Leadership & team" }]}
        eyebrow="Leadership & team"
        title="The people behind BunonBrain."
        lead="BunonBrain is built by a team that combines software engineering with an understanding of how garment factories work."
      />
      <Section>
        <Container wide>
          {LEADERS.length === 0 ? (
            <EmptyState
              icon="users"
              title="Team profiles are on their way"
              body="Leadership and team profiles will be published here once they are confirmed. For introductions, partnership or media requests, please get in touch."
              action={
                <ButtonLink href="/contact" arrow>
                  Contact us
                </ButtonLink>
              }
            />
          ) : (
            <ul className="grid gap-6 md:grid-cols-3">
              {LEADERS.map((l) => (
                <li key={l.name} className="rounded-2xl border border-brand-border p-7">
                  <p className="font-heading text-[1.2rem] font-semibold text-brand-text">{l.name}</p>
                  <p className="text-brand-emerald-ink">{l.role}</p>
                  <p className="mt-3 leading-relaxed text-brand-body">{l.bio}</p>
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
