import Link from "next/link";
import { ArrowRight, Box, Languages, ScrollText, ShieldCheck, Users2, Workflow } from "lucide-react";
import { ButtonLink, Card, CheckList, Container, CtaBand, Eyebrow, IconTile, Section, SectionHeader, StatusBadge } from "@/components/site/ui";
import { FaqList, PlatformDiagram, ScreenFrame, Steps } from "@/components/site/visuals";
import { FAQS } from "@/content/site/faqs";
import { MODULES } from "@/content/site/modules";
import { SOLUTIONS } from "@/content/site/solutions";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Home",
  description:
    "BunonBrain is the operations system for Bangladesh's garment factories: production planning, live line status, quality inspection, merchandising, approvals and workforce records in one role-based platform.",
  path: "/",
});

const FACTS = [
  { icon: Users2, title: "Eight roles", body: "From Super Admin to Worker, scoped by factory and line." },
  { icon: ScrollText, title: "Every decision audited", body: "Approvals and access changes go to an append-only log." },
  { icon: Box, title: "Starts with your records", body: "Output files, documents and photos. Hardware later." },
  { icon: Languages, title: "English and বাংলা", body: "One interface for the office and the floor." },
];

const CHALLENGES = [
  { title: "Information arrives after the hour is lost", body: "A stopped machine or a pile of bundles is noticed on a floor walk and reported at the end of the shift." },
  { title: "Plans drift from the floor", body: "Plans live in spreadsheets and are compared with actual output days later, when it is too late to rebalance." },
  { title: "Quality is recorded on paper", body: "Defects are tallied by hand, summarised weekly, and hard to trace to a line and an hour." },
  { title: "Decisions leave no record", body: "Changes to plans and line assignments happen verbally, so nobody can say later who decided and why." },
];

const WHY = [
  { icon: Workflow, title: "People decide, software proposes", body: "Agents and vision models recommend. An authorised person approves, and the decision is recorded." },
  { icon: ShieldCheck, title: "Access that matches the org chart", body: "Factory and line scope enforced by the server on every request, not just hidden in the interface." },
  { icon: ScrollText, title: "Numbers you can trace", body: "Efficiency, WIP, DHU and delivery risk calculated from records, with nothing estimated or filled in." },
];

export default function HomePage() {
  const capabilities = MODULES.slice(0, 9);
  return (
    <>
      {/* 1 · Hero */}
      <section aria-labelledby="hero-title" className="site-hero relative overflow-hidden border-b border-brand-border">
        <div aria-hidden className="site-grid pointer-events-none absolute inset-0" />
        <Container wide className="relative grid items-center gap-14 pb-20 pt-14 md:pt-20 lg:grid-cols-[minmax(0,0.95fr)_minmax(0,1.05fr)] lg:pb-28">
          <div className="site-rise">
            <p className="inline-flex items-center gap-2 rounded-full border border-brand-emerald/25 bg-white/80 px-3 py-1 font-plexmono text-[0.7rem] uppercase tracking-[0.14em] text-brand-emerald-ink">
              <span className="size-1.5 rounded-full bg-brand-emerald" aria-hidden /> Bangladesh RMG Workforce OS
            </p>
            <h1 id="hero-title" className="mt-6 font-heading text-[2.7rem] font-semibold leading-[1.03] tracking-[-0.03em] text-brand-text sm:text-[3.4rem] xl:text-[4.1rem]">
              One operating system for the whole <span className="text-brand-emerald-ink">garment floor.</span>
            </h1>
            <p className="mt-6 max-w-[34rem] text-[1.1rem] leading-[1.7] text-brand-body md:text-[1.2rem]">
              BunonBrain connects production plans, line status, quality inspections, buyer documents and the people who
              run them, so every decision on the floor is made on the same, current information.
            </p>
            <div className="mt-9 flex flex-wrap items-center gap-3">
              <ButtonLink href="/demo" size="lg" arrow>
                Request a demo
              </ButtonLink>
              <ButtonLink href="/product" size="lg" variant="secondary">
                Explore the product
              </ButtonLink>
            </div>
          </div>
          <div className="site-rise site-rise-2 relative">
            <div aria-hidden className="absolute -inset-6 -z-0 rounded-[2rem] bg-gradient-to-br from-brand-emerald-light/70 via-white/0 to-brand-navy/[0.06] blur-2xl" />
            <ScreenFrame src="/site/screens/overview.jpg" alt="BunonBrain floor overview: lines below target, mean line efficiency, machines down, pending decisions and the line status board, on simulated data" priority className="relative" />
          </div>
        </Container>
      </section>

      {/* 2 · Introduction */}
      <section aria-labelledby="intro-title" className="border-b border-brand-border bg-white py-16 md:py-20">
        <Container wide className="grid gap-12 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.6fr)]">
          <div>
            <Eyebrow>Introducing BunonBrain</Eyebrow>
            <h2 id="intro-title" className="mt-3 font-heading text-[1.9rem] font-semibold leading-tight tracking-[-0.02em] text-brand-text md:text-[2.3rem]">
              Built around how a Bangladeshi garment plant actually works.
            </h2>
          </div>
          <div>
            <p className="text-[1.08rem] leading-[1.8] text-brand-body">
              From the cutting table to the packing conveyor, a plant generates the information it needs to run well, but
              it rarely reaches the right person in time. BunonBrain is one system for that information: lines, orders,
              inspections and people, each visible to the roles responsible for them.
            </p>
            <ul className="mt-10 grid gap-6 sm:grid-cols-2">
              {FACTS.map((f) => (
                <li key={f.title} className="flex gap-4">
                  <span aria-hidden className="inline-flex size-10 shrink-0 items-center justify-center rounded-lg bg-brand-offwhite text-brand-navy ring-1 ring-brand-border">
                    <f.icon size={19} strokeWidth={1.8} />
                  </span>
                  <div>
                    <p className="font-heading text-[1.05rem] font-semibold text-brand-text">{f.title}</p>
                    <p className="mt-1 text-[0.95rem] leading-relaxed text-brand-body">{f.body}</p>
                  </div>
                </li>
              ))}
            </ul>
          </div>
        </Container>
      </section>

      {/* 3 · Challenges */}
      <Section tone="off" label="challenges-title">
        <Container wide>
          <SectionHeader
            id="challenges-title"
            eyebrow="The challenge"
            title="Garment factories run on information that arrives late."
            lead="Bangladesh's RMG plants work to tight buyer deadlines with thin margins. The problems below are common, and each one costs output, quality or a shipment date."
          />
          <div className="mt-14 grid gap-5 md:grid-cols-2">
            {CHALLENGES.map((c, i) => (
              <div key={c.title} className="flex gap-5 rounded-2xl border border-brand-border bg-white p-7">
                <span className="font-plexmono text-[0.85rem] text-brand-emerald-ink">{String(i + 1).padStart(2, "0")}</span>
                <div>
                  <h3 className="font-heading text-[1.2rem] font-semibold leading-snug text-brand-text">{c.title}</h3>
                  <p className="mt-2 leading-relaxed text-brand-body">{c.body}</p>
                </div>
              </div>
            ))}
          </div>
        </Container>
      </Section>

      {/* 4 · Solutions */}
      <Section label="solutions-title">
        <Container wide>
          <div className="flex flex-wrap items-end justify-between gap-6">
            <SectionHeader id="solutions-title" eyebrow="Our solutions" title="One platform, five outcomes." lead="Each solution combines the modules a part of the plant needs." />
            <ButtonLink href="/solutions" variant="secondary" arrow>
              All solutions
            </ButtonLink>
          </div>
          <div className="mt-12 grid gap-5 sm:grid-cols-2 lg:grid-cols-5">
            {SOLUTIONS.map((s) => (
              <Card key={s.slug} href={`/solutions/${s.slug}`} className="flex flex-col p-6">
                <IconTile name={s.icon} />
                <h3 className="mt-5 font-heading text-[1.1rem] font-semibold leading-snug text-brand-text">{s.name}</h3>
                <p className="mt-2 flex-1 text-[0.92rem] leading-relaxed text-brand-body">{s.short}</p>
                <span className="mt-5 inline-flex items-center gap-1 text-[0.88rem] font-medium text-brand-emerald-ink">
                  Learn more <ArrowRight size={14} aria-hidden className="transition-transform group-hover:translate-x-0.5" />
                </span>
              </Card>
            ))}
          </div>
        </Container>
      </Section>

      {/* 5 · Capabilities */}
      <Section tone="off" label="capabilities-title">
        <Container wide>
          <SectionHeader
            id="capabilities-title"
            eyebrow="Product capabilities"
            title="What BunonBrain does today."
            lead="Every module is labelled with its status, so you know exactly what is available, what is a demonstration and what is planned."
          />
          <div className="mt-12 grid gap-4 md:grid-cols-2 lg:grid-cols-3">
            {capabilities.map((m) => (
              <Card key={m.slug} href={`/product/${m.slug}`} className="p-6">
                <div className="flex items-start justify-between gap-4">
                  <IconTile name={m.icon} />
                  <StatusBadge status={m.status} />
                </div>
                <h3 className="mt-5 font-heading text-[1.12rem] font-semibold text-brand-text">{m.name}</h3>
                <p className="mt-2 text-[0.94rem] leading-relaxed text-brand-body">{m.short}</p>
              </Card>
            ))}
          </div>
          <div className="mt-10 text-center">
            <ButtonLink href="/product/features" variant="secondary" arrow>
              See all features
            </ButtonLink>
          </div>
        </Container>
      </Section>

      {/* 6 · How it works */}
      <Section label="how-title">
        <Container wide>
          <SectionHeader id="how-title" eyebrow="How it works" title="From the records you keep to decisions you can stand behind." align="center" />
          <div className="mt-14 rounded-3xl border border-brand-border bg-brand-offwhite p-4 sm:p-8">
            <PlatformDiagram />
          </div>
          <div className="mt-16">
            <Steps
              items={[
                { title: "Connect", body: "Start with output records, order documents and inspection photos. Add machine readings when you are ready." },
                { title: "See", body: "Line status, quality and order risk are calculated from those records and shared by role." },
                { title: "Decide", body: "Agents and models propose; authorised people approve or reject in one queue." },
                { title: "Record", body: "Decisions and access changes are written to an audit log that can't be edited." },
              ]}
            />
          </div>
          <div className="mt-12 text-center">
            <ButtonLink href="/how-it-works" variant="link" arrow>
              Read how BunonBrain works
            </ButtonLink>
          </div>
        </Container>
      </Section>

      {/* 7 · Workforce intelligence */}
      <Section tone="mint" label="workforce-title">
        <Container wide className="grid items-center gap-14 lg:grid-cols-[minmax(0,0.9fr)_minmax(0,1.1fr)]">
          <div>
            <SectionHeader
              id="workforce-title"
              eyebrow="Workforce intelligence"
              title="People are the plant. Their records belong next to the lines they run."
              lead="Worker profiles, line and workstation assignments and sign-in accounts live in the same system as production, under the same access rules."
            />
            <CheckList
              className="mt-8"
              items={[
                "Worker profiles with employment details visible only to permitted roles",
                "Workstation assignment within each manager's scope",
                "Workers see their own record through a linked account",
                <>
                  Roll-call, leave, overtime, skills and wellbeing as an interactive demo on sample data{" "}
                  <StatusBadge status="demo" className="ml-1 align-middle" />
                </>,
              ]}
            />
            <div className="mt-8">
              <ButtonLink href="/solutions/workforce-management" arrow>
                Workforce management
              </ButtonLink>
            </div>
          </div>
          <ScreenFrame
            src="/site/screens/workforce.jpg"
            alt="Workforce, HR and wellbeing demo: headcount, attendance, overtime, grievances and leave approvals on browser-only sample data"
            caption="Workforce demo · browser-only sample data, not connected to the API"
          />
        </Container>
      </Section>

      {/* 8 · Factory operations */}
      <Section tone="navy" label="ops-title" className="overflow-hidden">
        <div aria-hidden className="site-weave pointer-events-none absolute inset-0 opacity-50" />
        <Container wide className="relative grid items-center gap-14 lg:grid-cols-[minmax(0,1.1fr)_minmax(0,0.9fr)]">
          <ScreenFrame src="/site/screens/overview.jpg" alt="BunonBrain overview with floor status, key measures and the line status board on simulated data" caption="" className="order-last lg:order-first" />
          <div>
            <Eyebrow className="text-[rgb(var(--brand-emerald-on-dark))]">Factory operations</Eyebrow>
            <h2 id="ops-title" className="mt-3 font-heading text-[2rem] font-semibold leading-[1.1] tracking-[-0.02em] sm:text-[2.5rem]">
              Every line, every alert, one shared view.
            </h2>
            <p className="mt-5 text-[1.08rem] leading-relaxed text-white/75">
              The line board shows efficiency against target, WIP and non-productive time for each line. Alerts are stored
              by the server and pushed to every open screen, and a short morning brief ranks the day&apos;s issues.
            </p>
            <CheckList
              tone="dark"
              className="mt-8"
              items={["Line status flagged on track, watch or critical", "Real-time floor alerts shared across the team", "Machine, energy and RFID readings through the ingest API", "A morning brief of top issues and pending decisions"]}
            />
            <div className="mt-9 flex flex-wrap gap-3">
              <ButtonLink href="/solutions/factory-operations" arrow>
                Factory operations
              </ButtonLink>
              <ButtonLink href="/how-it-works" variant="onDark">
                How it works
              </ButtonLink>
            </div>
          </div>
        </Container>
      </Section>

      {/* 9 · Why BunonBrain */}
      <Section label="why-title">
        <Container wide>
          <div className="grid gap-12 lg:grid-cols-[minmax(0,0.8fr)_minmax(0,1.2fr)]">
            <SectionHeader
              id="why-title"
              eyebrow="Why BunonBrain"
              title="Designed to be trusted on a production floor."
              lead="Factories can't run on black boxes. These principles shape every module."
            />
            <div className="grid gap-5">
              {WHY.map((w) => (
                <div key={w.title} className="flex gap-5 rounded-2xl border border-brand-border p-6">
                  <span aria-hidden className="inline-flex size-11 shrink-0 items-center justify-center rounded-xl bg-brand-navy text-white">
                    <w.icon size={20} strokeWidth={1.8} />
                  </span>
                  <div>
                    <h3 className="font-heading text-[1.15rem] font-semibold text-brand-text">{w.title}</h3>
                    <p className="mt-1.5 leading-relaxed text-brand-body">{w.body}</p>
                  </div>
                </div>
              ))}
              <ButtonLink href="/why-bunonbrain" variant="link" arrow className="justify-start">
                More on our approach
              </ButtonLink>
            </div>
          </div>
        </Container>
      </Section>

      {/* 10 · Interface showcase */}
      <Section tone="off" label="showcase-title">
        <Container wide>
          <SectionHeader id="showcase-title" eyebrow="Product interface" title="Clear screens for busy people." lead="Real screens from the BunonBrain application, running on simulated data." align="center" />
          <div className="mt-14 grid gap-10 lg:grid-cols-2">
            <div>
              <ScreenFrame src="/site/screens/approvals.jpg" alt="Approvals queue: pending vision repair recommendations with reasoning, recommended actions and approve or reject buttons" caption="" />
              <h3 className="mt-6 font-heading text-[1.2rem] font-semibold text-brand-text">Approvals</h3>
              <p className="mt-1.5 leading-relaxed text-brand-body">Recommendations with their reasoning and a clear decision for an accountable person.</p>
            </div>
            <div>
              <ScreenFrame src="/site/screens/qc.jpg" alt="Quality inspections list with reference, stage, status, predictions and verified defects" caption="" />
              <h3 className="mt-6 font-heading text-[1.2rem] font-semibold text-brand-text">Quality inspections</h3>
              <p className="mt-1.5 leading-relaxed text-brand-body">Model predictions next to inspectors&apos; verified outcomes, inspection by inspection.</p>
            </div>
          </div>
        </Container>
      </Section>

      {/* 11 · FAQ */}
      <Section label="faq-title">
        <Container className="grid gap-12 lg:grid-cols-[minmax(0,0.75fr)_minmax(0,1.25fr)]">
          <div>
            <SectionHeader id="faq-title" eyebrow="FAQ" title="Questions we hear often." />
            <ButtonLink href="/faq" variant="link" arrow className="mt-6">
              All questions
            </ButtonLink>
          </div>
          <FaqList items={FAQS.slice(0, 5).map((f) => ({ q: f.q, a: f.a }))} />
        </Container>
      </Section>

      {/* 12 · Demo CTA */}
      <CtaBand />
    </>
  );
}
