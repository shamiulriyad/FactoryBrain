import { ButtonLink, Card, Container, EmptyState, PageHero, Section, SectionHeader } from "@/components/site/ui";
import { InquiryForm } from "@/components/site/InquiryForm";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Press & media",
  description: "Press resources for BunonBrain: brand assets, company facts and media enquiries.",
  path: "/press",
});

export default function PressPage() {
  return (
    <>
      <PageHero crumbs={[{ label: "Company", href: "/company" }, { label: "Press & media" }]} eyebrow="Press & media" title="Resources for journalists and media." lead="Brand assets, company facts and a direct line for media enquiries." />
      <Section label="kit-title">
        <Container wide>
          <SectionHeader id="kit-title" eyebrow="Media kit" title="Assets and facts" />
          <div className="mt-10 grid gap-5 md:grid-cols-3">
            <Card href="/brand" className="p-7">
              <p className="font-heading text-[1.15rem] font-semibold text-brand-text">Logo and brand files</p>
              <p className="mt-2 text-brand-body">Logo, symbol, colours and typography, with downloadable SVG and PNG files.</p>
            </Card>
            <Card href="/company" className="p-7">
              <p className="font-heading text-[1.15rem] font-semibold text-brand-text">Company fact sheet</p>
              <p className="mt-2 text-brand-body">What BunonBrain is, who it serves and what the product does today.</p>
            </Card>
            <Card href="/product" className="p-7">
              <p className="font-heading text-[1.15rem] font-semibold text-brand-text">Product overview</p>
              <p className="mt-2 text-brand-body">Modules, status labels and real interface screenshots on demo data.</p>
            </Card>
          </div>
        </Container>
      </Section>
      <Section tone="off" label="cov-title">
        <Container wide>
          <SectionHeader id="cov-title" eyebrow="Coverage" title="In the press" />
          <div className="mt-10">
            <EmptyState icon="file" title="No press coverage published yet" body="Articles and interviews about BunonBrain will be listed here." />
          </div>
        </Container>
      </Section>
      <Section label="mq-title">
        <Container wide className="grid gap-12 lg:grid-cols-[minmax(0,0.8fr)_minmax(0,1.2fr)]">
          <div>
            <SectionHeader id="mq-title" eyebrow="Media enquiries" title="Talk to us." lead="Interview requests, background briefings and fact checks." />
            <ButtonLink href="/brand" variant="link" arrow className="mt-4">
              Download brand assets
            </ButtonLink>
          </div>
          <div className="rounded-2xl border border-brand-border p-6 md:p-8">
            <InquiryForm kind="contact" subject="Press enquiry" fields={["company", "jobTitle"]} messageLabel="Your enquiry" submitLabel="Send enquiry" />
          </div>
        </Container>
      </Section>
    </>
  );
}
