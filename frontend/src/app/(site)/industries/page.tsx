import Link from "next/link";
import { Container, CtaBand, PageHero, Section, SectionHeader } from "@/components/site/ui";
import { moduleBySlug } from "@/content/site/modules";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Industries we serve",
  description: "BunonBrain is built for Bangladesh's ready-made garment industry: knitwear, woven garments, denim, sweaters and multi-factory export groups.",
  path: "/industries",
});

const SEGMENTS = [
  {
    name: "Knitwear",
    examples: "T-shirts, polos, hoodies, sweatshirts, sportswear",
    body: "High-volume lines with short operations and frequent style changes. Line balancing, hourly output against target and WIP between operations matter most.",
    modules: ["production-planning", "line-monitoring", "quality-inspection"],
  },
  {
    name: "Woven garments",
    examples: "Shirts, blouses, trousers, workwear",
    body: "Longer operation sequences and more complex specifications. Confirmed order details from POs and tech packs, and traceable quality, carry the most weight.",
    modules: ["merchandising", "production-planning", "quality-inspection"],
  },
  {
    name: "Denim and bottoms",
    examples: "Jeans, chinos, cargo trousers",
    body: "Heavy fabrics, washing and finishing stages, and strict measurement checks. Inspection records and delivery risk against wash and finishing time help most.",
    modules: ["quality-inspection", "production-planning", "reports-analytics"],
  },
  {
    name: "Sweaters",
    examples: "Flat-knit and fully fashioned knitwear",
    body: "Knitting, linking and finishing sections with skilled operators. Worker skills, workstation assignment and machine uptime are central.",
    modules: ["workforce", "line-monitoring", "ai-agents"],
  },
  {
    name: "Multi-factory export groups",
    examples: "Groups running several units for international buyers",
    body: "Several factories under one management team. Factory-scoped access, consistent measures across units and one approval and audit trail are essential.",
    modules: ["access-control", "reports-analytics", "approvals-workflow"],
  },
];

export default function IndustriesPage() {
  return (
    <>
      <PageHero
        crumbs={[{ label: "Industries" }]}
        eyebrow="Industries we serve"
        title="Built for Bangladesh's ready-made garment industry."
        lead="BunonBrain is designed for cut-and-sew and knitted garment production for export. The core is the same for every plant; what matters most differs by product."
      />
      <Section>
        <Container wide className="space-y-5">
          {SEGMENTS.map((s, i) => (
            <article key={s.name} aria-labelledby={`seg-${i}`} className="grid gap-6 rounded-2xl border border-brand-border p-7 md:grid-cols-[minmax(0,0.9fr)_minmax(0,1.3fr)_minmax(0,0.9fr)] md:p-9">
              <div>
                <p className="font-plexmono text-[0.72rem] text-brand-emerald-ink">{String(i + 1).padStart(2, "0")}</p>
                <h2 id={`seg-${i}`} className="mt-2 font-heading text-[1.6rem] font-semibold tracking-[-0.01em] text-brand-text">
                  {s.name}
                </h2>
                <p className="mt-2 text-[0.92rem] text-brand-muted">{s.examples}</p>
              </div>
              <p className="leading-relaxed text-brand-body md:text-[1.05rem]">{s.body}</p>
              <div>
                <p className="font-plexmono text-[0.68rem] uppercase tracking-[0.14em] text-brand-muted">Most relevant modules</p>
                <ul className="mt-3 space-y-1.5">
                  {s.modules.map((m) => (
                    <li key={m}>
                      <Link href={`/product/${m}`} className="text-[0.95rem] font-medium text-brand-navy underline-offset-4 hover:text-brand-emerald-ink hover:underline">
                        {moduleBySlug(m)?.name}
                      </Link>
                    </li>
                  ))}
                </ul>
              </div>
            </article>
          ))}
        </Container>
      </Section>
      <Section tone="off" label="bd-title">
        <Container wide className="grid gap-12 lg:grid-cols-2">
          <SectionHeader id="bd-title" eyebrow="Made for Bangladesh" title="Local by design." />
          <div className="space-y-5 text-[1.05rem] leading-relaxed text-brand-body">
            <p>
              The interface is available in English and বাংলা. Production dates follow the plant&apos;s own time zone, and the
              roles mirror how Bangladeshi plants are organised, from factory administration and HR to line supervisors and
              workers.
            </p>
            <p>
              BunonBrain starts from the records plants already keep and works without new hardware, so it fits factories
              at different stages of digitisation.
            </p>
          </div>
        </Container>
      </Section>
      <CtaBand title="Tell us how your plant is set up" body="A consultation is a working session on your product mix, lines and priorities." primary={{ label: "Book a consultation", href: "/consultation" }} secondary={{ label: "Request a demo", href: "/demo" }} />
    </>
  );
}
