import Link from "next/link";
import { ArticleCard } from "@/components/site/ArticleCard";
import { Container, CtaBand, PageHero, Section } from "@/components/site/ui";
import { ARTICLES, type ArticleCategory } from "@/content/site/articles";
import { pageMeta } from "@/lib/site/seo";
import { cn } from "@/lib/cn";

export const metadata = pageMeta({
  title: "Blog",
  description: "Articles from the BunonBrain team on garment production, quality, line balancing and responsible use of software on the factory floor.",
  path: "/blog",
});

const CATS: ("All" | ArticleCategory)[] = ["All", "RMG insights", "Guides", "Product"];
const CAT_HREF: Record<string, string> = { All: "/blog", "RMG insights": "/insights", Guides: "/guides", Product: "/blog#product" };

export default function BlogPage() {
  const [first, ...rest] = ARTICLES;
  return (
    <>
      <PageHero crumbs={[{ label: "Resources", href: "/resources" }, { label: "Blog" }]} eyebrow="Blog" title="Notes from the garment floor." lead="Explainers, guides and product thinking from the BunonBrain team." />
      <Section className="py-14 md:py-16">
        <Container wide>
          <nav aria-label="Categories" className="flex flex-wrap gap-2">
            {CATS.map((c) => (
              <Link
                key={c}
                href={CAT_HREF[c]}
                aria-current={c === "All" ? "page" : undefined}
                className={cn(
                  "inline-flex min-h-10 items-center rounded-full border px-4 text-[0.88rem] font-medium",
                  c === "All" ? "border-brand-navy bg-brand-navy text-white" : "border-brand-border text-brand-body hover:border-brand-navy/30",
                )}
              >
                {c}
              </Link>
            ))}
          </nav>
          <div className="mt-10">
            <ArticleCard a={first} large />
          </div>
          <div className="mt-5 grid gap-5 md:grid-cols-2 lg:grid-cols-3">
            {rest.map((a) => (
              <div key={a.slug} id={a.category === "Product" ? "product" : undefined} className="scroll-mt-28">
                <ArticleCard a={a} />
              </div>
            ))}
          </div>
        </Container>
      </Section>
      <CtaBand />
    </>
  );
}
