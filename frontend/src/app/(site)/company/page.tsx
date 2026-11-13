import Link from "next/link";
import { Card, Container, CtaBand, PageHero, Section, SectionHeader } from "@/components/site/ui";
import { MODULES } from "@/content/site/modules";
import { SITE } from "@/lib/site/config";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Company overview",
  description: "BunonBrain at a glance: what the company builds, who it serves, the product's status and where to find company information.",
  path: "/company",
});

export default function CompanyPage() {
  const available = MODULES.filter((m) => m.status === "available").length;
  const facts: [string, React.ReactNode][] = [
    ["Company", SITE.legal.entityName ?? "BunonBrain"],
    ["What we build", SITE.category],
    ["Industry", "Ready-made garments (RMG) and textile manufacturing"],
    ["Market", "Bangladesh garment exporters"],
    ["Product", `Web application with ${MODULES.length} modules (${available} available today)`],
    ["Languages", "English and বাংলা"],
    ["Tagline", SITE.tagline],
    ["Registered office", SITE.legal.registeredAddress ?? <span className="site-tbc">To be published</span>],
  ];
  const links = [
    ["About BunonBrain", "/about", "What we build and why."],
    ["Mission & vision", "/about/mission", "Where we are going."],
    ["Our values", "/company/values", "What we hold ourselves to."],
    ["Leadership & team", "/company/leadership", "The people behind BunonBrain."],
    ["Careers", "/careers", "Work with us."],
    ["News & press", "/news", "Announcements and media."],
    ["Partnerships", "/partners", "Build with us."],
    ["Brand", "/brand", "Logo, colours and files."],
  ];
  return (
    <>
      <PageHero crumbs={[{ label: "Company" }]} eyebrow="Company overview" title="BunonBrain at a glance." lead={SITE.description} />
      <Section label="facts-title">
        <Container wide className="grid gap-12 lg:grid-cols-[minmax(0,0.8fr)_minmax(0,1.2fr)]">
          <SectionHeader id="facts-title" eyebrow="Fact sheet" title="Key facts" />
          <dl className="divide-y divide-brand-border rounded-2xl border border-brand-border">
            {facts.map(([k, v]) => (
              <div key={k} className="grid gap-1 px-6 py-4 sm:grid-cols-[12rem_1fr]">
                <dt className="font-plexmono text-[0.74rem] uppercase tracking-[0.12em] text-brand-muted">{k}</dt>
                <dd className="font-medium text-brand-text">{v}</dd>
              </div>
            ))}
          </dl>
        </Container>
      </Section>
      <Section tone="off" label="more-title">
        <Container wide>
          <h2 id="more-title" className="font-heading text-[1.6rem] font-semibold text-brand-text">
            More about the company
          </h2>
          <div className="mt-8 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            {links.map(([t, href, b]) => (
              <Card key={href} href={href} className="p-6">
                <p className="font-heading text-[1.1rem] font-semibold text-brand-text">{t}</p>
                <p className="mt-1.5 text-[0.93rem] text-brand-body">{b}</p>
              </Card>
            ))}
          </div>
          <p className="mt-8 text-[0.92rem] text-brand-muted">
            Media and partnership enquiries:{" "}
            <Link href="/contact" className="text-brand-emerald-ink underline underline-offset-2">
              contact us
            </Link>
            .
          </p>
        </Container>
      </Section>
      <CtaBand />
    </>
  );
}
