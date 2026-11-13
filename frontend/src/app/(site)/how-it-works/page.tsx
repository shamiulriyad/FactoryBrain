import { ButtonLink, CheckList, Container, CtaBand, PageHero, Section, SectionHeader } from "@/components/site/ui";
import { PlatformDiagram, ScreenFrame, Steps } from "@/components/site/visuals";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "How it works",
  description: "How BunonBrain goes from the records a garment factory already keeps to shared line status, calculated measures, approved decisions and an audit trail.",
  path: "/how-it-works",
});

const PHASES = [
  {
    n: "01",
    title: "Set up the plant",
    body: "An administrator creates the factory, its production lines, shifts and styles with SMV, and invites people with the roles they need. Nothing is pre-filled with assumed values.",
    points: ["Factories, lines, shifts and styles", "User accounts with one or more of eight roles", "Factory and line scope for every account"],
  },
  {
    n: "02",
    title: "Bring in the data you already have",
    body: "Start with the records the plant keeps today. Output is entered or imported, order documents and garment photos are uploaded, and documents are added to the knowledge base.",
    points: ["Output entry and file import", "PO, tech pack and spec sheet uploads", "Inspection photos", "Machine, energy and RFID readings via the ingest API, when ready"],
  },
  {
    n: "03",
    title: "See the same picture",
    body: "BunonBrain calculates line status, efficiency from SMV, WIP, defect rates, DHU and delivery risk from those records, and shows each role what it is responsible for.",
    points: ["Line board and floor alerts in real time", "Quality measures from reviewed inspections", "Order risk against delivery dates"],
  },
  {
    n: "04",
    title: "Decide, and keep the record",
    body: "Agents and vision models recommend. Plans, line-balancing proposals and agent actions wait in the approval queue for an authorised decision, which is written to the audit log.",
    points: ["Risk-tiered approvals", "Reasons required for rejection", "Append-only audit log"],
  },
];

export default function HowItWorksPage() {
  return (
    <>
      <PageHero
        crumbs={[{ label: "How it works" }]}
        eyebrow="How it works"
        title="From the records you keep to decisions you can stand behind."
        lead="BunonBrain is introduced one step at a time. It starts with information the plant already has and adds sources and automation only when people trust what they see."
        actions={
          <ButtonLink href="/demo" size="lg" arrow>
            See it on a running plant
          </ButtonLink>
        }
      />
      <Section>
        <Container wide>
          <Steps items={PHASES.map((p) => ({ title: p.title, body: p.body }))} />
        </Container>
      </Section>
      {PHASES.map((p, i) => (
        <Section key={p.n} tone={i % 2 === 0 ? "off" : "white"} label={`ph-${p.n}`} className="py-16 md:py-20">
          <Container wide className="grid items-center gap-12 lg:grid-cols-2">
            <div className={i % 2 ? "lg:order-last" : undefined}>
              <p className="font-plexmono text-[3rem] font-medium leading-none text-brand-emerald/30">{p.n}</p>
              <h2 id={`ph-${p.n}`} className="mt-4 font-heading text-[2rem] font-semibold tracking-[-0.02em] text-brand-text">
                {p.title}
              </h2>
              <p className="mt-4 text-[1.08rem] leading-relaxed text-brand-body">{p.body}</p>
              <CheckList className="mt-7" items={p.points} />
            </div>
            {i === 0 && <PlatformDiagram className="rounded-2xl border border-brand-border bg-white p-4" />}
            {i === 1 && (
              <div className="rounded-2xl border border-brand-border bg-white p-8">
                <p className="font-plexmono text-[0.72rem] uppercase tracking-[0.14em] text-brand-muted">No hardware needed to start</p>
                <p className="mt-4 font-heading text-[1.4rem] font-semibold leading-snug text-brand-text">
                  A built-in simulator produces realistic line and machine readings, so BunonBrain can be evaluated before anything is connected.
                </p>
              </div>
            )}
            {i === 2 && <ScreenFrame src="/site/screens/overview.jpg" alt="Floor overview with line status and key measures, simulated data" />}
            {i === 3 && <ScreenFrame src="/site/screens/approvals.jpg" alt="Approvals queue with reasoning and recommended actions, demo data" />}
          </Container>
        </Section>
      ))}
      <Section label="impl-title">
        <Container wide className="grid gap-12 lg:grid-cols-2">
          <SectionHeader
            id="impl-title"
            eyebrow="Rolling it out"
            title="Start on one floor."
            lead="We recommend beginning with one floor or a few lines, with the records that are easiest to collect, and widening once supervisors rely on what they see."
          />
          <CheckList
            items={[
              "Agree the first lines and the measures that matter most to you",
              "Set up lines, shifts and styles, and invite the first users",
              "Run alongside existing reports until the numbers match",
              "Add documents, inspections and readings step by step",
            ]}
          />
        </Container>
      </Section>
      <CtaBand />
    </>
  );
}
