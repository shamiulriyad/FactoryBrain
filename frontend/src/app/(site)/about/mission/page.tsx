import { CheckList, Container, CtaBand, PageHero, Section, SectionHeader } from "@/components/site/ui";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Our mission & vision",
  description: "BunonBrain's mission is to give every garment factory one trustworthy view of its operations, and the tools to act on it responsibly.",
  path: "/about/mission",
});

export default function MissionPage() {
  return (
    <>
      <PageHero crumbs={[{ label: "About", href: "/about" }, { label: "Mission & vision" }]} eyebrow="Mission & vision" title="Why BunonBrain exists, and where it is going." />
      <Section label="mission-title">
        <Container wide className="grid gap-12 lg:grid-cols-[minmax(0,0.8fr)_minmax(0,1.2fr)]">
          <SectionHeader id="mission-title" eyebrow="Our mission" title="One trustworthy view, used responsibly." />
          <div>
            <p className="font-heading text-[1.6rem] font-semibold leading-snug text-brand-text md:text-[1.9rem]">
              Give every garment factory one trustworthy view of its operations, and the tools to act on it responsibly.
            </p>
            <p className="mt-6 text-[1.05rem] leading-relaxed text-brand-body">In practice, that means:</p>
            <CheckList
              className="mt-4"
              items={[
                "Information reaches the person responsible for a line, an order or a worker while it can still change the outcome.",
                "Measures are calculated the same way everywhere, from records anyone can check.",
                "Automation supports decisions but never takes them away from accountable people.",
                "Personal and commercial information is visible only to the roles that need it.",
              ]}
            />
          </div>
        </Container>
      </Section>
      <Section tone="off" label="vision-title">
        <Container wide className="grid gap-12 lg:grid-cols-[minmax(0,0.8fr)_minmax(0,1.2fr)]">
          <SectionHeader id="vision-title" eyebrow="Our vision" title="Every floor, on current and shared information." />
          <div>
            <p className="font-heading text-[1.6rem] font-semibold leading-snug text-brand-text md:text-[1.9rem]">
              A Bangladeshi garment industry where decisions on every floor are made on current, shared and verifiable information.
            </p>
            <div className="mt-8 grid gap-5 sm:grid-cols-3">
              {[
                ["For factories", "Fewer surprises, steadier output and quality that can be shown to buyers."],
                ["For workers", "Fair, transparent records and assignments, with their own data protected."],
                ["For the industry", "A stronger, more competitive RMG sector built on reliable information."],
              ].map(([t, b]) => (
                <div key={t} className="rounded-2xl border border-brand-border bg-white p-6">
                  <p className="font-heading font-semibold text-brand-text">{t}</p>
                  <p className="mt-2 text-[0.95rem] leading-relaxed text-brand-body">{b}</p>
                </div>
              ))}
            </div>
          </div>
        </Container>
      </Section>
      <CtaBand title="Explore what BunonBrain does today" body="See the modules that put the mission into practice." primary={{ label: "Explore the product", href: "/product" }} secondary={{ label: "Request a demo", href: "/demo" }} />
    </>
  );
}
