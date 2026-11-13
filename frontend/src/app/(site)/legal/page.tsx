import { Card, Container, PageHero, Section } from "@/components/site/ui";
import { LEGAL } from "@/content/site/legal";
import { formatDate } from "@/content/site/articles";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Legal & policies",
  description: "BunonBrain's privacy policy, terms of service, cookie, data protection, acceptable use, security, data retention, disclaimer, intellectual property and accessibility policies.",
  path: "/legal",
});

export default function LegalIndex() {
  const drafts = LEGAL.filter((l) => l.status === "draft").length;
  return (
    <>
      <PageHero crumbs={[{ label: "Legal" }]} eyebrow="Legal & policies" title="Policies and terms" lead="How BunonBrain handles data, security and the use of the service." />
      <Section className="py-14 md:py-16">
        <Container wide>
          {drafts > 0 && (
            <div className="mb-10 rounded-2xl border border-brand-warning/40 bg-brand-warning/10 p-6 text-brand-body">
              <p className="font-medium text-brand-text">These documents are drafts.</p>
              <p className="mt-1 text-[0.95rem]">
                They describe how the BunonBrain product works today and are awaiting legal review. Highlighted text marks details still to
                be confirmed by the company.
              </p>
            </div>
          )}
          <div className="grid gap-4 md:grid-cols-2">
            {LEGAL.map((l) => (
              <Card key={l.slug} href={`/legal/${l.slug}`} className="p-6">
                <div className="flex items-center justify-between gap-3">
                  <p className="font-heading text-[1.15rem] font-semibold text-brand-text">{l.title}</p>
                  {l.status === "draft" && (
                    <span className="rounded-full border border-brand-warning/40 bg-brand-warning/10 px-2.5 py-0.5 font-plexmono text-[0.66rem] uppercase tracking-[0.08em] text-[rgb(var(--brand-warning-ink))]">
                      Draft
                    </span>
                  )}
                </div>
                <p className="mt-2 text-[0.94rem] leading-relaxed text-brand-body">{l.summary}</p>
                <p className="mt-4 text-[0.82rem] text-brand-muted">Last updated {formatDate(l.updated)}</p>
              </Card>
            ))}
          </div>
        </Container>
      </Section>
    </>
  );
}
