import { Container, EmptyState, PageHero, Section, SectionHeader } from "@/components/site/ui";
import { InquiryForm } from "@/components/site/InquiryForm";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Partnerships",
  description: "Partner with BunonBrain: implementation and consulting partners, technology and hardware integrations, and industry and academic collaboration.",
  path: "/partners",
});

const TYPES = [
  { title: "Implementation & consulting", body: "Industrial engineering, lean and digital-transformation consultancies who help plants adopt BunonBrain." },
  { title: "Technology & hardware", body: "Providers of machine sensors, RFID bundle tracking, energy meters and ERP systems that can feed the ingest API." },
  { title: "Industry & research", body: "Associations, training institutes and universities working on productivity, quality and worker wellbeing in RMG." },
];

export default function PartnersPage() {
  return (
    <>
      <PageHero
        crumbs={[{ label: "Company", href: "/company" }, { label: "Partnerships" }]}
        eyebrow="Partnerships"
        title="Build the future of RMG operations with us."
        lead="No single company can digitise a garment industry. We're open to partners who share our commitment to practical, honest technology for the floor."
      />
      <Section label="types-title">
        <Container wide>
          <SectionHeader id="types-title" eyebrow="Ways to partner" title="Who we'd like to work with" />
          <div className="mt-10 grid gap-5 md:grid-cols-3">
            {TYPES.map((t) => (
              <div key={t.title} className="rounded-2xl border border-brand-border p-7">
                <h2 className="font-heading text-[1.2rem] font-semibold text-brand-text">{t.title}</h2>
                <p className="mt-2 leading-relaxed text-brand-body">{t.body}</p>
              </div>
            ))}
          </div>
          <div className="mt-12">
            <EmptyState icon="layers" title="Partner directory" body="No partnerships have been announced yet. Confirmed partners will be listed here." />
          </div>
        </Container>
      </Section>
      <Section tone="off" label="pf-title">
        <Container wide className="grid gap-12 lg:grid-cols-[minmax(0,0.8fr)_minmax(0,1.2fr)]">
          <SectionHeader id="pf-title" eyebrow="Get in touch" title="Propose a partnership" lead="Tell us about your organisation and what you have in mind." />
          <div className="rounded-2xl border border-brand-border bg-white p-6 md:p-8">
            <InquiryForm
              kind="partnership"
              fields={["company", "jobTitle", "phone"]}
              extra={[{ key: "type", label: "Partnership type", type: "select", required: true, options: TYPES.map((t) => ({ value: t.title, label: t.title })).concat({ value: "Other", label: "Other" }) }]}
              messageLabel="Your proposal"
              submitLabel="Send proposal"
            />
          </div>
        </Container>
      </Section>
    </>
  );
}
