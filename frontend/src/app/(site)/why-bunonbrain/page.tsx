import { Eye, FileCheck2, Languages, Layers, ShieldCheck, Workflow } from "lucide-react";
import { ButtonLink, Container, CtaBand, PageHero, Section, SectionHeader } from "@/components/site/ui";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Why BunonBrain",
  description: "Why garment factories choose BunonBrain: human decisions, access enforced by the server, traceable numbers, honest labelling, local design and a gradual start.",
  path: "/why-bunonbrain",
});

const REASONS = [
  { icon: Workflow, title: "People decide; software proposes", body: "Agents and vision models produce recommendations. Anything that changes a plan or an assignment needs an authorised approval, and rejection needs a reason." },
  { icon: ShieldCheck, title: "Access enforced on the server", body: "Eight roles, factory scope and line scope are checked by the API on every request. Hiding a button is never the only control." },
  { icon: FileCheck2, title: "Numbers that trace to records", body: "Efficiency, WIP, DHU and delivery risk are calculated from recorded output, reviewed inspections and confirmed order data. Nothing is estimated or filled in." },
  { icon: Eye, title: "Honest about what is real", body: "Simulated data is labelled as simulated, demo screens say they are demos, and AI without a configured provider says so instead of guessing." },
  { icon: Languages, title: "Designed for Bangladesh", body: "English and বাংলা, plant time zones, and roles that mirror how Bangladeshi garment factories are organised." },
  { icon: Layers, title: "Start small, grow safely", body: "Begin with the records you have on one floor. Add documents, inspections and machine readings when you're ready." },
];

const COMPARE = [
  ["Plans and actuals", "Separate spreadsheets, compared later", "One plan of record, compared as output is recorded"],
  ["Line status", "Found on floor walks", "Shared line board and real-time alerts"],
  ["Quality data", "Paper tallies, weekly summaries", "Photo records, reviewed findings, calculated DHU"],
  ["Order details", "Attachments in email", "Versioned documents with confirmed, sourced values"],
  ["Decisions", "Verbal, unrecorded", "Approval queue and append-only audit log"],
  ["Access", "Shared files and links", "Role, factory and line scope on every request"],
];

export default function WhyPage() {
  return (
    <>
      <PageHero
        crumbs={[{ label: "Why BunonBrain" }]}
        eyebrow="Why BunonBrain"
        title="Software a production floor can trust."
        lead="Garment factories don't need another dashboard. They need one reliable record of how the plant runs, and tools that support the people accountable for it."
        actions={
          <ButtonLink href="/demo" size="lg" arrow>
            Request a demo
          </ButtonLink>
        }
      />
      <Section>
        <Container wide className="grid gap-5 md:grid-cols-2 lg:grid-cols-3">
          {REASONS.map((r) => (
            <div key={r.title} className="rounded-2xl border border-brand-border p-7">
              <span aria-hidden className="inline-flex size-11 items-center justify-center rounded-xl bg-brand-navy text-white">
                <r.icon size={20} strokeWidth={1.8} />
              </span>
              <h2 className="mt-5 font-heading text-[1.25rem] font-semibold text-brand-text">{r.title}</h2>
              <p className="mt-2 leading-relaxed text-brand-body">{r.body}</p>
            </div>
          ))}
        </Container>
      </Section>
      <Section tone="off" label="cmp-title">
        <Container wide>
          <SectionHeader id="cmp-title" eyebrow="Before and after" title="What changes in day-to-day work" />
          <div className="mt-12 overflow-x-auto rounded-2xl border border-brand-border bg-white">
            <table className="w-full min-w-[640px] text-left">
              <caption className="sr-only">Typical way of working compared with BunonBrain</caption>
              <thead>
                <tr className="border-b border-brand-border bg-brand-offwhite text-[0.85rem]">
                  <th scope="col" className="px-6 py-4 font-medium text-brand-muted">Area</th>
                  <th scope="col" className="px-6 py-4 font-medium text-brand-muted">Typical today</th>
                  <th scope="col" className="px-6 py-4 font-medium text-brand-emerald-ink">With BunonBrain</th>
                </tr>
              </thead>
              <tbody>
                {COMPARE.map(([a, b, c]) => (
                  <tr key={a} className="border-b border-brand-border last:border-0">
                    <th scope="row" className="px-6 py-4 font-heading font-semibold text-brand-text">{a}</th>
                    <td className="px-6 py-4 text-brand-body">{b}</td>
                    <td className="px-6 py-4 font-medium text-brand-text">{c}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Container>
      </Section>
      <CtaBand />
    </>
  );
}
