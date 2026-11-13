import Link from "next/link";
import { Container, CtaBand, IconTile, PageHero, Section, StatusBadge } from "@/components/site/ui";
import { MODULES } from "@/content/site/modules";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Features & capabilities",
  description: "Every BunonBrain capability, module by module, with its status: available, demo preview or planned.",
  path: "/product/features",
});

export default function FeaturesPage() {
  const total = MODULES.reduce((n, m) => n + m.capabilities.length, 0);
  return (
    <>
      <PageHero
        crumbs={[{ label: "Product", href: "/product" }, { label: "Features & capabilities" }]}
        eyebrow="Features & capabilities"
        title="Everything BunonBrain does, in one list."
        lead={`${total} capabilities across ${MODULES.length} modules. Each one is marked with its status, so there is no guessing about what you would get today.`}
      >
        <ul className="mt-8 flex flex-wrap gap-3" aria-label="Status legend">
          {(["available", "demo", "planned"] as const).map((s) => (
            <li key={s} className="flex items-center gap-2 text-[0.9rem] text-brand-body">
              <StatusBadge status={s} />
              {s === "available" && "In the product today"}
              {s === "demo" && "Interactive demonstration on sample data"}
              {s === "planned" && "In development, no committed date"}
            </li>
          ))}
        </ul>
      </PageHero>

      <Section>
        <Container wide>
          <nav aria-label="Jump to module" className="mb-12 flex flex-wrap gap-2">
            {MODULES.map((m) => (
              <a key={m.slug} href={`#${m.slug}`} className="inline-flex min-h-9 items-center rounded-full border border-brand-border px-3.5 text-[0.85rem] text-brand-body hover:border-brand-navy/30 hover:text-brand-text">
                {m.name}
              </a>
            ))}
          </nav>
          <div className="space-y-6">
            {MODULES.map((m) => (
              <section key={m.slug} id={m.slug} aria-labelledby={`${m.slug}-t`} className="scroll-mt-28 rounded-2xl border border-brand-border p-6 md:p-8">
                <div className="flex flex-wrap items-center gap-4">
                  <IconTile name={m.icon} />
                  <h2 id={`${m.slug}-t`} className="font-heading text-[1.4rem] font-semibold text-brand-text">
                    <Link href={`/product/${m.slug}`} className="hover:text-brand-emerald-ink">
                      {m.name}
                    </Link>
                  </h2>
                  <StatusBadge status={m.status} />
                </div>
                <table className="mt-6 w-full text-left text-[0.95rem]">
                  <caption className="sr-only">{m.name} capabilities</caption>
                  <thead className="sr-only">
                    <tr>
                      <th scope="col">Capability</th>
                      <th scope="col">Description</th>
                      <th scope="col">Status</th>
                    </tr>
                  </thead>
                  <tbody>
                    {m.capabilities.map((c) => (
                      <tr key={c.title} className="border-t border-brand-border align-top">
                        <th scope="row" className="w-[30%] py-3.5 pr-4 font-medium text-brand-text">
                          {c.title}
                        </th>
                        <td className="py-3.5 pr-4 leading-relaxed text-brand-body">{c.body}</td>
                        <td className="w-32 py-3.5 text-right">
                          <StatusBadge status={c.status ?? m.status} />
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </section>
            ))}
          </div>
        </Container>
      </Section>
      <CtaBand />
    </>
  );
}
