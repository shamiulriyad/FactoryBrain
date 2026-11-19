import Link from "next/link";
import { ArrowRight, BadgeCheck, Box, Clock3, FileSpreadsheet, MonitorSmartphone, ScrollText, ShieldCheck, Users2, Workflow } from "lucide-react";
import { ButtonLink, Card, CheckList, Container, CtaBand, Eyebrow, IconTile, Section, SectionHeader, StatusBadge } from "@/components/site/ui";
import { FaqList, PlatformDiagram, ScreenFrame, Steps } from "@/components/site/visuals";
import { HeroProduct } from "@/components/site/HeroProduct";
import { DemoVideo } from "@/components/site/DemoVideo";
import { getDemoVideo } from "@/lib/site/demoVideo";
import { FAQS } from "@/content/site/faqs";
import { MODULES, type IconName } from "@/content/site/modules";
import { SOLUTIONS } from "@/content/site/solutions";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Home",
  description:
    "BunonBrain is the operations system for Bangladesh's garment factories: production planning, live floor and machine monitoring, camera AI, quality inspection, merchandising, documents, approvals and workforce records in one role-based platform.",
  path: "/",
});

const FACTS = [
  { icon: Users2, title: "Eight roles", body: "From Super Admin to Worker, scoped by factory and line." },
  { icon: ScrollText, title: "Every decision audited", body: "Approvals and access changes go to an append-only log." },
  { icon: Box, title: "Starts with your records", body: "Output files, documents and photos. Sensors when you are ready." },
  { icon: MonitorSmartphone, title: "Runs in a browser", body: "Nothing to install for the office or the floor." },
];

const CHALLENGES = [
  { title: "Information arrives after the hour is lost", body: "A stopped machine or a pile of bundles is noticed on a floor walk and reported at the end of the shift." },
  { title: "Plans drift from the floor", body: "Plans live in spreadsheets and are compared with actual output days later, when it is too late to rebalance." },
  { title: "Quality is recorded on paper", body: "Defects are tallied by hand, summarised weekly, and hard to trace to a line and an hour." },
  { title: "Decisions leave no record", body: "Changes to plans and line assignments happen verbally, so nobody can say later who decided and why." },
];

const LIVE: { icon: IconName; title: string; body: string; href: string }[] = [
  { icon: "layers", title: "Live floor map", body: "Every production floor with its people, machines, open alerts and an OK / Watch / Act status.", href: "/product/floor-map-brain" },
  { icon: "camera", title: "Monitoring room & camera AI", body: "Cameras, devices and alerts as they happen; camera frames checked for people, helmets and vests.", href: "/product/monitoring-camera-ai" },
  { icon: "cpu", title: "Machines over MQTT", body: "Sensors and piece counters show each machine running, idle, stopped or in error, with real counts.", href: "/product/machine-monitoring" },
  { icon: "brain", title: "Factory Brain", body: "Floors, lines, machines, orders, buyers and risks drawn as one connected map.", href: "/product/floor-map-brain" },
];

const WHY = [
  { icon: Workflow, title: "People decide, software proposes", body: "Agents and vision models recommend. An authorised person approves, and the decision is recorded." },
  { icon: ShieldCheck, title: "Access that matches the org chart", body: "Factory and line scope enforced by the server on every request, not just hidden in the interface." },
  { icon: ScrollText, title: "Numbers you can trace", body: "Efficiency, WIP, DHU and delivery risk calculated from records, with nothing estimated or filled in." },
];

const LOCAL = [
  { icon: MonitorSmartphone, title: "Low cost to adopt", body: "Runs in a browser, with low-cost ESP32 sensors and the CCTV cameras a plant already has." },
  { icon: Clock3, title: "Dhaka time by default", body: "Work dates, lateness and overtime follow each factory's time zone." },
  { icon: FileSpreadsheet, title: "The paperwork plants file", body: "AQL, GRN, packing lists and attendance sheets generated from the records." },
  { icon: BadgeCheck, title: "Roles of an RMG plant", body: "Merchandiser, QC, HR, supervisor and manager, each with their own menu." },
];

export default async function HomePage() {
  // The demo video is a setting (Administration → Website), not code.
  const video = await getDemoVideo();
  const available = MODULES.filter((m) => m.status === "available");
  const stats = [
    { value: String(available.length), label: "modules in production", note: "each with its status on its page" },
    { value: "8", label: "roles", note: "scoped by factory and line" },
    { value: "38", label: "factory documents", note: "generated from your records" },
    { value: "0", label: "installs", note: "runs in a web browser" },
  ];

  return (
    <>
      {/* 1 · Hero */}
      <section aria-labelledby="hero-title" className="site-hero relative overflow-hidden border-b border-brand-border">
        <div aria-hidden className="site-grid pointer-events-none absolute inset-0" />
        <Container wide className="relative grid items-center gap-16 pb-20 pt-16 md:pt-24 lg:grid-cols-[minmax(0,0.8fr)_minmax(0,1.2fr)] lg:gap-12 lg:pb-28">
          <div className="site-rise">
            <h1 id="hero-title" className="font-heading text-[2.6rem] font-semibold leading-[1.04] tracking-[-0.03em] text-brand-text sm:text-[3.3rem] xl:text-[3.9rem]">
              One operating system for the whole <span className="text-brand-emerald-ink">garment floor.</span>
            </h1>
            <p className="mt-6 max-w-[33rem] text-[1.08rem] leading-[1.7] text-brand-body md:text-[1.18rem]">
              Production planning, live floor and machine status, quality inspection, buyer documents, attendance and
              approvals in one ERP built for Bangladesh&apos;s garment factories.
            </p>
            <div className="mt-9 flex flex-wrap items-center gap-3">
              <ButtonLink href="/demo" size="lg" arrow>
                Request a demo
              </ButtonLink>
              <ButtonLink href="#demo-video" size="lg" variant="secondary">
                Watch the demo
              </ButtonLink>
            </div>
            <ul className="mt-10 flex max-w-[34rem] flex-wrap gap-x-6 gap-y-3 border-t border-brand-border pt-6 text-[0.9rem] text-brand-body">
              {["Role-based access", "Runs in a browser", "Every decision audited"].map((t) => (
                <li key={t} className="inline-flex items-center gap-2 whitespace-nowrap">
                  <BadgeCheck size={16} aria-hidden className="shrink-0 text-brand-emerald" />
                  {t}
                </li>
              ))}
            </ul>
          </div>
          <div className="site-rise site-rise-2 relative lg:pl-4 xl:pl-8">
            <div aria-hidden className="absolute -inset-8 -z-0 rounded-[2.5rem] bg-gradient-to-br from-brand-emerald-light/70 via-white/0 to-brand-marigold-light/80 blur-2xl" />
            <HeroProduct />
          </div>
        </Container>
      </section>

      {/* 2 · Numbers */}
      <section aria-label="BunonBrain in numbers" className="border-b border-brand-border bg-white">
        <Container wide>
          <dl className="grid grid-cols-2 divide-brand-border lg:grid-cols-4 lg:divide-x">
            {stats.map((s, i) => (
              <div key={s.label} className={`px-2 py-9 sm:px-6 ${i % 2 === 1 ? "border-l border-brand-border lg:border-l-0" : ""} ${i >= 2 ? "border-t border-brand-border lg:border-t-0" : ""}`}>
                <dt className="sr-only">{s.label}</dt>
                <dd>
                  <span className="font-heading text-[2.6rem] font-semibold leading-none tracking-[-0.03em] text-brand-marigold-ink md:text-[3.1rem]">{s.value}</span>
                  <span className="mt-2 block font-heading text-[1.02rem] font-semibold text-brand-text">{s.label}</span>
                  <span className="mt-0.5 block text-[0.88rem] text-brand-muted">{s.note}</span>
                </dd>
              </div>
            ))}
          </dl>
        </Container>
      </section>

      {/* 3 · Demo video (set under Administration → Website) */}
      <section id="demo-video" aria-labelledby="video-title" className="relative overflow-hidden border-b border-brand-border bg-brand-offwhite py-20 md:py-24">
        <div aria-hidden className="site-grid pointer-events-none absolute inset-0 opacity-70" />
        <Container wide className="relative">
          <SectionHeader
            id="video-title"
            eyebrow="Product demo"
            title="See BunonBrain at work."
            lead="A short walkthrough of BunonBrain on a running garment plant: planning, the floor, quality and the people who run them, in one system."
            align="center"
          />
          <div className="relative mx-auto mt-12 max-w-[60rem]">
            <div aria-hidden className="absolute -inset-4 -z-0 rounded-[1.75rem] bg-gradient-to-br from-brand-emerald-light/80 via-transparent to-brand-marigold-light blur-xl" />
            <div className="relative rounded-[1.25rem] border border-brand-border bg-white p-2 shadow-[0_30px_80px_-30px_rgba(16,38,61,0.45)] sm:p-3">
              <DemoVideo videoId={video?.videoId} title={video?.title} />
            </div>
          </div>
          <div className="mt-10 flex flex-wrap justify-center gap-3">
            <ButtonLink href="/demo" arrow>
              Request a live demo
            </ButtonLink>
            <ButtonLink href="/how-it-works" variant="secondary">
              How it works
            </ButtonLink>
          </div>
        </Container>
      </section>

      {/* 4 · Introduction */}
      <section aria-labelledby="intro-title" className="border-b border-brand-border bg-white py-16 md:py-20">
        <Container wide className="grid gap-12 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.6fr)]">
          <div>
            <Eyebrow>Introducing BunonBrain</Eyebrow>
            <h2 id="intro-title" className="mt-3 font-heading text-[1.9rem] font-semibold leading-tight tracking-[-0.02em] text-brand-text md:text-[2.3rem]">
              Built around how a Bangladeshi garment plant actually works.
            </h2>
            <div aria-hidden className="site-thread mt-7 w-20" />
          </div>
          <div>
            <p className="text-[1.08rem] leading-[1.8] text-brand-body">
              From the cutting table to the packing conveyor, a plant generates the information it needs to run well, but
              it rarely reaches the right person in time. BunonBrain is one system for that information: lines, machines,
              orders, inspections and people, each visible to the roles responsible for them.
            </p>
            <ul className="mt-10 grid gap-6 sm:grid-cols-2">
              {FACTS.map((f, i) => (
                <li key={f.title} className="flex gap-4">
                  <span
                    aria-hidden
                    className={`inline-flex size-10 shrink-0 items-center justify-center rounded-lg ring-1 ${i % 2 === 0 ? "bg-brand-offwhite text-brand-navy ring-brand-border" : "bg-brand-marigold-light text-brand-marigold-ink ring-brand-marigold/20"}`}
                  >
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

      {/* 4 · Challenges */}
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
              <div key={c.title} className="relative flex gap-5 overflow-hidden rounded-2xl border border-brand-border bg-white p-7">
                <span aria-hidden className="absolute inset-y-0 left-0 w-1 bg-gradient-to-b from-brand-marigold to-brand-marigold/0" />
                <span className="font-heading text-[1.6rem] font-semibold leading-none text-brand-marigold-ink">{String(i + 1).padStart(2, "0")}</span>
                <div>
                  <h3 className="font-heading text-[1.2rem] font-semibold leading-snug text-brand-text">{c.title}</h3>
                  <p className="mt-2 leading-relaxed text-brand-body">{c.body}</p>
                </div>
              </div>
            ))}
          </div>
        </Container>
      </Section>

      {/* 5 · Solutions */}
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

      {/* 6 · Live from the floor */}
      <Section tone="warm" label="live-title" className="overflow-hidden">
        <Container wide>
          <SectionHeader
            id="live-title"
            eyebrow="Live from the floor"
            title="See the building, the machines and the cameras in one place."
            lead="Status is rolled up from real records: alerts, machine sensors, piece counters and attendance. Simulated data is always labelled and never sets a status."
          />
          <div className="mt-14 grid items-start gap-10 lg:grid-cols-[minmax(0,1.25fr)_minmax(0,0.75fr)]">
            <ScreenFrame src="/site/screens/floor-map.jpg" alt="BunonBrain live floor map: three production floors with status, checked-in workers, machines, open alerts and cameras" caption="Live floor map · sample factory data" />
            <ul className="grid gap-4">
              {LIVE.map((l) => (
                <li key={l.title}>
                  <Link href={l.href} className="group flex gap-4 rounded-2xl border border-brand-border bg-white/90 p-5 transition-[border-color,box-shadow] hover:border-brand-marigold/40 hover:shadow-[0_12px_32px_-16px_rgba(180,83,9,0.35)]">
                    <IconTile name={l.icon} tone="warm" />
                    <div>
                      <h3 className="font-heading text-[1.05rem] font-semibold text-brand-text">{l.title}</h3>
                      <p className="mt-1 text-[0.93rem] leading-relaxed text-brand-body">{l.body}</p>
                      <span className="mt-2 inline-flex items-center gap-1 text-[0.85rem] font-medium text-brand-emerald-ink">
                        Learn more <ArrowRight size={13} aria-hidden className="transition-transform group-hover:translate-x-0.5" />
                      </span>
                    </div>
                  </Link>
                </li>
              ))}
            </ul>
          </div>
          <div className="mt-12 grid gap-8 md:grid-cols-2">
            <ScreenFrame src="/site/screens/brain.jpg" alt="Factory Brain graph linking floors, lines, machines, orders, buyers, styles and targets" caption="Factory Brain · sample factory data" />
            <ScreenFrame src="/site/screens/monitoring.jpg" alt="Monitoring room with cameras, devices, active alerts and a live event feed, simulated events labelled" caption="Monitoring room · simulated events are labelled" />
          </div>
        </Container>
      </Section>

      {/* 7 · Capabilities */}
      <Section tone="off" label="capabilities-title">
        <Container wide>
          <div className="flex flex-wrap items-end justify-between gap-6">
            <SectionHeader
              id="capabilities-title"
              eyebrow="Product capabilities"
              title="What BunonBrain does today."
              lead="Every module is labelled with its status, so you know exactly what is available and what is planned."
            />
            <ButtonLink href="/product/features" variant="secondary" arrow>
              See all features
            </ButtonLink>
          </div>
          <div className="mt-12 grid gap-4 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
            {available.map((m, i) => (
              <Card key={m.slug} href={`/product/${m.slug}`} className="p-6">
                <div className="flex items-start justify-between gap-4">
                  <IconTile name={m.icon} tone={i % 4 === 3 ? "warm" : "light"} />
                  <StatusBadge status={m.status} />
                </div>
                <h3 className="mt-5 font-heading text-[1.08rem] font-semibold text-brand-text">{m.name}</h3>
                <p className="mt-2 text-[0.92rem] leading-relaxed text-brand-body">{m.short}</p>
              </Card>
            ))}
          </div>
        </Container>
      </Section>

      {/* 8 · How it works */}
      <Section label="how-title">
        <Container wide>
          <SectionHeader id="how-title" eyebrow="How it works" title="From the records you keep to decisions you can stand behind." align="center" />
          <div className="mt-14 rounded-3xl border border-brand-border bg-brand-offwhite p-4 sm:p-8">
            <PlatformDiagram />
          </div>
          <div className="mt-16">
            <Steps
              items={[
                { title: "Connect", body: "Start with output records, order documents and inspection photos. Add sensors, counters and cameras when you are ready." },
                { title: "See", body: "Floor, line, machine, quality and order status are calculated from those records and shared by role." },
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

      {/* 9 · Workforce */}
      <Section tone="mint" label="workforce-title">
        <Container wide className="grid items-center gap-14 lg:grid-cols-[minmax(0,0.9fr)_minmax(0,1.1fr)]">
          <div>
            <SectionHeader
              id="workforce-title"
              eyebrow="Workforce and attendance"
              title="People are the plant. Their records belong next to the lines they run."
              lead="Worker profiles, workstation assignments, attendance, leave and overtime live in the same system as production, under the same access rules."
            />
            <CheckList
              className="mt-8"
              items={[
                "Worker register with employment details visible only to permitted roles",
                "RFID check-in and check-out, shifts, breaks and holidays",
                "Overtime requested and approved; leave recorded as approved documents",
                "Workstation assignment within each manager's scope, with full history",
                "A Workforce hub with headcount, today's attendance and performance",
              ]}
            />
            <div className="mt-8 flex flex-wrap gap-3">
              <ButtonLink href="/product/workforce" arrow>
                Workforce management
              </ButtonLink>
              <ButtonLink href="/product/attendance-shifts" variant="secondary">
                Attendance & shifts
              </ButtonLink>
            </div>
          </div>
          <ScreenFrame
            src="/site/screens/workforce.jpg"
            alt="BunonBrain Workforce hub: active workers, present today, overtime awaiting approval and headcount by department and line"
            caption="Workforce hub · sample factory data"
          />
        </Container>
      </Section>

      {/* 10 · Factory operations */}
      <Section tone="navy" label="ops-title" className="overflow-hidden">
        <div aria-hidden className="site-weave pointer-events-none absolute inset-0 opacity-50" />
        <Container wide className="relative grid items-center gap-14 lg:grid-cols-[minmax(0,1.1fr)_minmax(0,0.9fr)]">
          <ScreenFrame src="/site/screens/overview.jpg" alt="BunonBrain overview with floor status, key measures and the line status board" caption="" className="order-last lg:order-first" />
          <div>
            <Eyebrow>Factory operations</Eyebrow>
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
              items={[
                "Line status flagged on track, watch or critical",
                "Real-time floor alerts shared across the team",
                "Machine states and piece counts from sensors over MQTT",
                "A morning brief of top issues and pending decisions",
              ]}
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

      {/* 11 · Made for Bangladesh */}
      <Section tone="warm" label="local-title">
        <Container wide className="grid items-center gap-12 lg:grid-cols-[minmax(0,0.85fr)_minmax(0,1.15fr)]">
          <div>
            <Eyebrow>Made for Bangladesh</Eyebrow>
            <h2 id="local-title" className="mt-3 font-heading text-[2rem] font-semibold leading-[1.1] tracking-[-0.02em] text-brand-text sm:text-[2.5rem]">
              <i>Bunon</i> means weaving.
              <span className="block text-brand-emerald-ink">The brain is the people who run the floor.</span>
            </h2>
            <p className="mt-5 max-w-[34rem] text-[1.05rem] leading-relaxed text-brand-body">
              BunonBrain is designed around a Bangladeshi export plant: knit and woven lines, cutting and finishing
              sections, buyer documents and a workforce in the thousands.
            </p>
            <p className="mt-6 font-plexmono text-[0.78rem] uppercase tracking-[0.16em] text-brand-marigold-ink">Smart Workforce. Stronger RMG.</p>
          </div>
          <ul className="grid gap-4 sm:grid-cols-2">
            {LOCAL.map((l) => (
              <li key={l.title} className="rounded-2xl border border-brand-marigold/20 bg-white/90 p-6">
                <span aria-hidden className="inline-flex size-10 items-center justify-center rounded-lg bg-brand-marigold-light text-brand-marigold-ink ring-1 ring-brand-marigold/20">
                  <l.icon size={19} strokeWidth={1.8} />
                </span>
                <h3 className="mt-4 font-heading text-[1.05rem] font-semibold text-brand-text">{l.title}</h3>
                <p className="mt-1.5 text-[0.93rem] leading-relaxed text-brand-body">{l.body}</p>
              </li>
            ))}
          </ul>
        </Container>
      </Section>

      {/* 12 · Why BunonBrain */}
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
                <div key={w.title} className="flex gap-5 rounded-2xl border border-brand-border p-6 transition-colors hover:border-brand-marigold/35">
                  <span aria-hidden className="inline-flex size-11 shrink-0 items-center justify-center rounded-xl bg-brand-navy text-brand-marigold-on-dark">
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

      {/* 13 · Interface showcase */}
      <Section tone="off" label="showcase-title">
        <Container wide>
          <SectionHeader id="showcase-title" eyebrow="Product interface" title="Clear screens for busy people." lead="Real screens from the BunonBrain application, running on sample factory data." align="center" />
          <div className="mt-14 grid gap-10 lg:grid-cols-3">
            {[
              { src: "/site/screens/approvals.jpg", alt: "Approvals queue: recommendations with reasoning, recommended actions and approve or reject buttons", title: "Approvals", body: "Recommendations with their reasoning and a clear decision for an accountable person." },
              { src: "/site/screens/qc.jpg", alt: "Quality inspections list with reference, stage, status, predictions and verified defects", title: "Quality inspections", body: "Model predictions next to inspectors' verified outcomes, inspection by inspection." },
              { src: "/site/screens/integrations.jpg", alt: "Integrations page with RFID readers, attendance card readers, machine sensors and production counters", title: "Integrations", body: "Every data source with its devices, records and when the last one arrived." },
            ].map((x) => (
              <div key={x.title}>
                <ScreenFrame src={x.src} alt={x.alt} caption="" />
                <h3 className="mt-6 font-heading text-[1.2rem] font-semibold text-brand-text">{x.title}</h3>
                <p className="mt-1.5 leading-relaxed text-brand-body">{x.body}</p>
              </div>
            ))}
          </div>
        </Container>
      </Section>

      {/* 14 · FAQ */}
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

      {/* 15 · Demo CTA */}
      <CtaBand />
    </>
  );
}
