import { Container, CtaBand, PageHero, Section } from "@/components/site/ui";
import { VALUES } from "@/content/site/company";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Our values",
  description: "The values behind BunonBrain: respect for the floor, evidence over estimates, accountable automation, honesty, privacy by role and design for Bangladesh.",
  path: "/company/values",
});

export default function ValuesPage() {
  return (
    <>
      <PageHero
        crumbs={[{ label: "Company", href: "/company" }, { label: "Our values" }]}
        eyebrow="Our values"
        title="Six commitments that shape the product."
        lead="Our values show up in the product itself: in what it calculates, what it labels, and who it lets decide."
      />
      <Section>
        <Container wide>
          <ol className="grid gap-px overflow-hidden rounded-3xl border border-brand-border bg-brand-border md:grid-cols-2">
            {VALUES.map((v, i) => (
              <li key={v.title} className="bg-white p-8 md:p-10">
                <p className="font-plexmono text-[0.8rem] text-brand-emerald-ink">{String(i + 1).padStart(2, "0")}</p>
                <h2 className="mt-3 font-heading text-[1.5rem] font-semibold tracking-[-0.01em] text-brand-text">{v.title}</h2>
                <p className="mt-3 text-[1.03rem] leading-relaxed text-brand-body">{v.body}</p>
              </li>
            ))}
          </ol>
        </Container>
      </Section>
      <CtaBand />
    </>
  );
}
