import { CheckList, Container, EmptyState, PageHero, Section, SectionHeader } from "@/components/site/ui";
import { InquiryForm } from "@/components/site/InquiryForm";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Careers",
  description: "Careers at BunonBrain. See open roles, or introduce yourself if you want to build technology for Bangladesh's garment industry.",
  path: "/careers",
});

/** Open roles: add verified listings here. */
const OPENINGS: { title: string; team: string; location: string; summary: string }[] = [];

export default function CareersPage() {
  return (
    <>
      <PageHero
        crumbs={[{ label: "Company", href: "/company" }, { label: "Careers" }]}
        eyebrow="Careers"
        title="Build technology for the people who make the world's clothes."
        lead="We are building software that thousands of supervisors, inspectors, merchandisers and workers will use every day. Careful, practical people do their best work here."
      />
      <Section label="open-title">
        <Container wide>
          <SectionHeader id="open-title" eyebrow="Open roles" title="Current openings" />
          <div className="mt-10">
            {OPENINGS.length === 0 ? (
              <EmptyState icon="users" title="No open positions right now" body="We don't have any openings at the moment. New roles will be listed here first. You are welcome to introduce yourself below." />
            ) : (
              <ul className="divide-y divide-brand-border rounded-2xl border border-brand-border">
                {OPENINGS.map((o) => (
                  <li key={o.title} className="p-6">
                    <p className="font-heading text-[1.15rem] font-semibold text-brand-text">{o.title}</p>
                    <p className="text-[0.9rem] text-brand-muted">
                      {o.team} · {o.location}
                    </p>
                    <p className="mt-2 text-brand-body">{o.summary}</p>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </Container>
      </Section>
      <Section tone="off" label="intro-title">
        <Container wide className="grid gap-12 lg:grid-cols-[minmax(0,0.8fr)_minmax(0,1.2fr)]">
          <div>
            <SectionHeader id="intro-title" eyebrow="Introduce yourself" title="Tell us what you'd like to work on." />
            <CheckList
              className="mt-8"
              items={["Software engineering (web, .NET, data)", "Product and design for factory users", "Industrial engineering and RMG operations", "Implementation and customer support"]}
            />
            <p className="mt-6 text-[0.9rem] text-brand-muted">Please don&apos;t send CVs or personal documents through this form; we will ask for them if there is a fit.</p>
          </div>
          <div className="rounded-2xl border border-brand-border bg-white p-6 md:p-8">
            <InquiryForm
              kind="contact"
              subject="Careers: introduction"
              fields={["jobTitle"]}
              extra={[{ key: "area", label: "Area of interest", type: "select", options: ["Engineering", "Product & design", "RMG operations", "Implementation & support", "Other"].map((v) => ({ value: v, label: v })) }]}
              messageLabel="About you"
              messagePlaceholder="Your background and what you'd like to work on. A link to a profile or portfolio is welcome."
              submitLabel="Send introduction"
            />
          </div>
        </Container>
      </Section>
    </>
  );
}
