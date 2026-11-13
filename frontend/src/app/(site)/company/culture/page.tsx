import { ButtonLink, Container, CtaBand, PageHero, Section, SectionHeader } from "@/components/site/ui";
import { WORKING_PRINCIPLES } from "@/content/site/company";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Our culture",
  description: "How the BunonBrain team works: go to the floor, write it down, ship in small safe steps, say what is not done, and treat security as everyone's job.",
  path: "/company/culture",
});

export default function CulturePage() {
  return (
    <>
      <PageHero
        crumbs={[{ label: "Company", href: "/company" }, { label: "Culture" }]}
        eyebrow="Our culture"
        title="How we work."
        lead="Building software for factories means working the way good factories do: close to the work, careful with quality, and clear about what is finished."
        actions={
          <ButtonLink href="/careers" variant="secondary" arrow>
            Careers
          </ButtonLink>
        }
      />
      <Section>
        <Container wide>
          <SectionHeader eyebrow="Working principles" title="Six habits we practise" />
          <div className="mt-12 grid gap-5 md:grid-cols-2 lg:grid-cols-3">
            {WORKING_PRINCIPLES.map((p) => (
              <div key={p.title} className="rounded-2xl border border-brand-border p-7">
                <h2 className="font-heading text-[1.2rem] font-semibold text-brand-text">{p.title}</h2>
                <p className="mt-2 leading-relaxed text-brand-body">{p.body}</p>
              </div>
            ))}
          </div>
        </Container>
      </Section>
      <CtaBand title="Interested in working with us?" body="We'd like to hear from people who care about manufacturing, software quality and Bangladesh's garment industry." primary={{ label: "Careers", href: "/careers" }} secondary={{ label: "Contact us", href: "/contact" }} />
    </>
  );
}
