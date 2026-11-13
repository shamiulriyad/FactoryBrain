import { Container, CtaBand, PageHero, Section, SectionHeader } from "@/components/site/ui";
import { MILESTONES } from "@/content/site/company";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Our story",
  description: "The story of the BunonBrain product: the factory it was designed around, the principles behind it and how its modules were built.",
  path: "/about/story",
});

export default function StoryPage() {
  return (
    <>
      <PageHero
        crumbs={[{ label: "About", href: "/about" }, { label: "Our story" }]}
        eyebrow="Our story"
        title="Built module by module around a real garment plant."
        lead="BunonBrain began with a simple question: what would a mid-sized Bangladeshi export plant need to see, every hour, to run better? Everything since has been built to answer it."
      />
      <Section label="origin-title">
        <Container wide className="grid gap-12 lg:grid-cols-[minmax(0,0.8fr)_minmax(0,1.2fr)]">
          <SectionHeader id="origin-title" eyebrow="The starting point" title="A plant, not a template." />
          <div className="space-y-5 text-[1.08rem] leading-[1.8] text-brand-body">
            <p>
              Instead of adapting a generic manufacturing product, BunonBrain was designed around the profile of a typical
              Bangladeshi export plant: around thirty knit and woven sewing lines, a cutting section, three finishing sections
              and roughly three thousand workers.
            </p>
            <p>
              That profile shaped the first decisions: the roles, the line-level scope of access, the measures that matter
              (efficiency from SMV, WIP, DHU, delivery risk) and the rule that the system starts from records a plant already
              keeps.
            </p>
          </div>
        </Container>
      </Section>
      <Section tone="off" label="ms-title">
        <Container wide>
          <SectionHeader id="ms-title" eyebrow="How the product has grown" title="Product milestones" lead="Taken from the application's own release history." />
          <ol className="relative mt-14 border-l border-brand-navy/15 pl-8 md:ml-4">
            {MILESTONES.map((m) => (
              <li key={m.title} className="relative pb-10 last:pb-0">
                <span aria-hidden className="absolute -left-[41px] top-1 size-4 rounded-full border-4 border-brand-offwhite bg-brand-emerald" />
                <p className="font-plexmono text-[0.75rem] uppercase tracking-[0.12em] text-brand-emerald-ink">{m.date}</p>
                <h3 className="mt-1 font-heading text-[1.25rem] font-semibold text-brand-text">{m.title}</h3>
                <p className="mt-1.5 max-w-[42rem] leading-relaxed text-brand-body">{m.body}</p>
              </li>
            ))}
          </ol>
        </Container>
      </Section>
      <Section label="co-title">
        <Container wide>
          <div className="rounded-2xl border border-dashed border-brand-navy/20 bg-brand-offwhite p-8">
            <h2 id="co-title" className="font-heading text-[1.3rem] font-semibold text-brand-text">
              Company history
            </h2>
            <p className="mt-2 max-w-[48rem] leading-relaxed text-brand-body">
              The company&apos;s founding details and history will be published here once they are confirmed. Until then,
              this page covers the product&apos;s story only.
            </p>
          </div>
        </Container>
      </Section>
      <CtaBand />
    </>
  );
}
