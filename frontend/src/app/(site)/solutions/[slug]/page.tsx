import { notFound } from "next/navigation";
import { ArrowRight } from "lucide-react";
import { ButtonLink, Card, Container, CtaBand, IconTile, PageHero, Section, SectionHeader, StatusBadge } from "@/components/site/ui";
import { ScreenFrame } from "@/components/site/visuals";
import { moduleBySlug } from "@/content/site/modules";
import { SOLUTIONS, solutionBySlug } from "@/content/site/solutions";
import { pageMeta } from "@/lib/site/seo";

export function generateStaticParams() {
  return SOLUTIONS.map((s) => ({ slug: s.slug }));
}

export async function generateMetadata({ params }: { params: Promise<{ slug: string }> }) {
  const s = solutionBySlug((await params).slug);
  if (!s) return {};
  return pageMeta({ title: s.name, description: `${s.headline} ${s.intro}`.slice(0, 300), path: `/solutions/${s.slug}` });
}

export default async function SolutionPage({ params }: { params: Promise<{ slug: string }> }) {
  const s = solutionBySlug((await params).slug);
  if (!s) notFound();
  const mods = s.modules.map(moduleBySlug).filter((m) => m !== undefined);
  const shot = mods.find((m) => m.screenshot)?.screenshot;
  const others = SOLUTIONS.filter((x) => x.slug !== s.slug);

  return (
    <>
      <PageHero
        crumbs={[{ label: "Solutions", href: "/solutions" }, { label: s.name }]}
        eyebrow={s.name}
        title={s.headline}
        lead={s.intro}
        actions={
          <>
            <ButtonLink href="/demo" size="lg" arrow>
              Request a demo
            </ButtonLink>
            <ButtonLink href="/consultation" size="lg" variant="secondary">
              Book a consultation
            </ButtonLink>
          </>
        }
        aside={shot ? <ScreenFrame src={shot.src} alt={shot.alt} priority /> : undefined}
      />

      <Section label="ch-title">
        <Container wide className="grid gap-14 lg:grid-cols-2">
          <div>
            <SectionHeader id="ch-title" eyebrow="The challenge" title="What gets in the way today" />
            <ul className="mt-10 space-y-6">
              {s.challenges.map((c) => (
                <li key={c.title} className="border-l-2 border-brand-error/50 pl-5">
                  <h3 className="font-heading text-[1.15rem] font-semibold text-brand-text">{c.title}</h3>
                  <p className="mt-1.5 leading-relaxed text-brand-body">{c.body}</p>
                </li>
              ))}
            </ul>
          </div>
          <div>
            <SectionHeader eyebrow="With BunonBrain" title="What changes" />
            <ul className="mt-10 space-y-6">
              {s.outcomes.map((c) => (
                <li key={c.title} className="border-l-2 border-brand-emerald pl-5">
                  <h3 className="font-heading text-[1.15rem] font-semibold text-brand-text">{c.title}</h3>
                  <p className="mt-1.5 leading-relaxed text-brand-body">{c.body}</p>
                </li>
              ))}
            </ul>
          </div>
        </Container>
      </Section>

      <Section tone="off" label="mods-title">
        <Container wide>
          <SectionHeader id="mods-title" eyebrow="Modules" title={`The modules behind ${s.name.toLowerCase()}`} />
          <div className="mt-12 grid gap-5 md:grid-cols-3">
            {mods.map((m) => (
              <Card key={m.slug} href={`/product/${m.slug}`} className="flex flex-col">
                <div className="flex items-start justify-between gap-3">
                  <IconTile name={m.icon} />
                  <StatusBadge status={m.status} />
                </div>
                <h3 className="mt-5 font-heading text-[1.2rem] font-semibold text-brand-text">{m.name}</h3>
                <p className="mt-2 flex-1 leading-relaxed text-brand-body">{m.summary}</p>
                <span className="mt-6 inline-flex items-center gap-1 text-[0.9rem] font-medium text-brand-emerald-ink">
                  Module details <ArrowRight size={14} aria-hidden />
                </span>
              </Card>
            ))}
          </div>
        </Container>
      </Section>

      <Section label="roles-title">
        <Container wide className="grid items-start gap-12 lg:grid-cols-[minmax(0,0.9fr)_minmax(0,1.1fr)]">
          <SectionHeader id="roles-title" eyebrow="Who it's for" title="Built for the people who run this part of the plant." />
          <ul className="grid gap-3 sm:grid-cols-3">
            {s.forRoles.map((r) => (
              <li key={r} className="rounded-xl border border-brand-border bg-brand-offwhite px-5 py-4 font-heading font-medium text-brand-text">
                {r}
              </li>
            ))}
          </ul>
        </Container>
      </Section>

      <Section tone="off" label="more-title">
        <Container wide>
          <h2 id="more-title" className="font-heading text-[1.4rem] font-semibold text-brand-text">
            Other solutions
          </h2>
          <ul className="mt-6 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            {others.map((o) => (
              <li key={o.slug}>
                <Card href={`/solutions/${o.slug}`} className="flex items-center gap-4 p-5">
                  <IconTile name={o.icon} />
                  <span className="font-medium text-brand-text">{o.name}</span>
                </Card>
              </li>
            ))}
          </ul>
        </Container>
      </Section>
      <CtaBand />
    </>
  );
}
