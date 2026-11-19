import Link from "next/link";
import { ButtonLink, CheckList, Container, CtaBand, Eyebrow, PageHero, Section, SectionHeader } from "@/components/site/ui";
import { VALUES } from "@/content/site/company";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "About BunonBrain",
  description: "BunonBrain builds the operations system for Bangladesh's ready-made garment factories. Learn what we are building, why, and how we approach technology on the factory floor.",
  path: "/about",
});

export default function AboutPage() {
  return (
    <>
      <PageHero
        crumbs={[{ label: "About" }]}
        eyebrow="About BunonBrain"
        title="Technology that respects how garments are made."
        lead="BunonBrain is an operations system for Bangladesh's ready-made garment factories. We build software that helps the people who run production lines see clearly, decide well and keep a record of why."
        actions={
          <>
            <ButtonLink href="/product" size="lg" arrow>
              Explore the product
            </ButtonLink>
            <ButtonLink href="/about/mission" size="lg" variant="secondary">
              Mission & vision
            </ButtonLink>
          </>
        }
      />

      <Section label="what-title">
        <Container wide className="grid gap-12 lg:grid-cols-[minmax(0,0.8fr)_minmax(0,1.2fr)]">
          <div>
            <SectionHeader id="what-title" eyebrow="What BunonBrain is" title="One record of how the plant runs." />
            <div aria-hidden className="site-thread mt-7 w-20" />
          </div>
          <div className="space-y-5 text-[1.08rem] leading-[1.8] text-brand-body">
            <p>
              BunonBrain is a single web application that brings production planning, line status, quality inspection,
              merchandising documents, approvals and workforce records together. Every module works from the same factories,
              lines, orders and people, and every account sees only what its role allows.
            </p>
            <p>
              It is designed around a typical Bangladeshi export plant: knit and woven lines, a cutting section, finishing
              sections and a workforce in the thousands. The name joins <i>bunon</i>, weaving, with the
              intelligence the system adds alongside the people who do the work.
            </p>
          </div>
        </Container>
      </Section>

      <Section tone="off" label="why-title">
        <Container wide className="grid gap-12 lg:grid-cols-2">
          <div>
            <SectionHeader id="why-title" eyebrow="Why we are building it" title="The information exists. It just arrives too late." />
            <p className="mt-6 text-[1.05rem] leading-relaxed text-brand-body">
              Ready-made garments are central to Bangladesh&apos;s economy and to millions of livelihoods. Plants compete on
              lead time, quality and compliance with very little margin for error, yet much of the information they run on
              still moves on paper, in spreadsheets and by word of mouth.
            </p>
          </div>
          <div className="relative overflow-hidden rounded-2xl border border-brand-border bg-white p-8">
            <span aria-hidden className="absolute inset-y-0 left-0 w-1 bg-gradient-to-b from-brand-marigold to-brand-marigold/0" />
            <Eyebrow>The problem we want to solve</Eyebrow>
            <CheckList
              className="mt-5"
              items={[
                "Line problems are discovered after the hour is already lost.",
                "Plans and actual output are compared too late to rebalance.",
                "Quality data can't be traced to the line and hour that produced it.",
                "Order details exist in several versions, none of them confirmed.",
                "Decisions are made verbally and leave no record.",
              ]}
            />
          </div>
        </Container>
      </Section>

      <Section label="mv-title">
        <Container wide>
          <h2 id="mv-title" className="sr-only">
            Mission and vision
          </h2>
          <div className="grid gap-6 md:grid-cols-2">
            <div className="site-cta relative overflow-hidden rounded-3xl p-10 text-white">
              <div aria-hidden className="site-weave pointer-events-none absolute inset-0" />
              <p className="site-eyebrow relative font-plexmono text-[0.72rem] uppercase tracking-[0.16em] text-brand-marigold-on-dark">Our mission</p>
              <p className="relative mt-4 font-heading text-[1.6rem] font-semibold leading-snug">
                Give every garment factory one trustworthy view of its operations, and the tools to act on it responsibly.
              </p>
            </div>
            <div className="rounded-3xl border border-brand-border bg-brand-emerald-light/40 p-10">
              <p className="site-eyebrow font-plexmono text-[0.72rem] uppercase tracking-[0.16em] text-brand-marigold-ink">Our vision</p>
              <p className="mt-4 font-heading text-[1.6rem] font-semibold leading-snug text-brand-text">
                A Bangladeshi garment industry where decisions on every floor are made on current, shared and verifiable information.
              </p>
            </div>
          </div>
          <p className="mt-6">
            <Link href="/about/mission" className="font-medium text-brand-emerald-ink underline-offset-4 hover:underline">
              Read our mission and vision in full
            </Link>
          </p>
        </Container>
      </Section>

      <Section tone="warm" label="values-title">
        <Container wide>
          <SectionHeader id="values-title" eyebrow="Our core values" title="What we hold ourselves to" />
          <div className="mt-12 grid gap-5 md:grid-cols-2 lg:grid-cols-3">
            {VALUES.map((v, i) => (
              <div key={v.title} className="relative overflow-hidden rounded-2xl border border-brand-marigold/20 bg-white/90 p-7">
                <span aria-hidden className="absolute inset-x-0 top-0 h-1 bg-gradient-to-r from-brand-marigold to-brand-emerald/60" />
                <span className="font-heading text-[1.4rem] font-semibold leading-none text-brand-marigold-ink">{String(i + 1).padStart(2, "0")}</span>
                <h3 className="mt-4 font-heading text-[1.2rem] font-semibold text-brand-text">{v.title}</h3>
                <p className="mt-2 leading-relaxed text-brand-body">{v.body}</p>
              </div>
            ))}
          </div>
        </Container>
      </Section>

      <Section label="approach-title">
        <Container wide className="grid gap-12 lg:grid-cols-2">
          <SectionHeader
            id="approach-title"
            eyebrow="Our approach to RMG technology"
            title="Floor first. Evidence always. People in charge."
            lead="We build for supervisors and managers who have little time and no patience for black boxes."
          />
          <CheckList
            items={[
              "Start from the records a plant already keeps; never require new hardware to begin.",
              "Calculate measures from records with documented formulas; never fill gaps with guesses.",
              "Let software recommend and people decide, with every decision recorded.",
              "Enforce access on the server, by role, factory and line.",
              "Label simulated, demo and planned features plainly.",
            ]}
          />
        </Container>
      </Section>

      <Section tone="navy" label="dir-title">
        <Container wide className="grid gap-12 lg:grid-cols-2">
          <div>
            <Eyebrow>Where we are going</Eyebrow>
            <h2 id="dir-title" className="mt-3 font-heading text-[2rem] font-semibold tracking-[-0.02em]">
              Our long-term direction
            </h2>
            <p className="mt-5 text-[1.05rem] leading-relaxed text-white/75">
              The foundation is in place: planning, line and machine monitoring, camera AI, quality, merchandising,
              document automation, approvals, access control, and a workforce layer with attendance, shifts, leave and
              overtime on the server. Next are wider data connections, such as direct PLC and Modbus links, so labour,
              machines and production are planned together from one record.
            </p>
          </div>
          <div className="flex flex-col items-start justify-end gap-3 sm:flex-row sm:items-end lg:justify-end">
            <ButtonLink href="/product" size="lg" arrow>
              Explore the product
            </ButtonLink>
            <ButtonLink href="/about/story" size="lg" variant="onDark">
              Our story
            </ButtonLink>
          </div>
        </Container>
      </Section>
      <CtaBand />
    </>
  );
}
