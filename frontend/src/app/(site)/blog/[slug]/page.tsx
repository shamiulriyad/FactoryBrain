import { notFound } from "next/navigation";
import { ArticleCard } from "@/components/site/ArticleCard";
import { Prose, Toc } from "@/components/site/Prose";
import { Breadcrumbs, Container, CtaBand, Eyebrow, Section } from "@/components/site/ui";
import { ARTICLES, articleBySlug, formatDate } from "@/content/site/articles";
import { SITE } from "@/lib/site/config";
import { pageMeta } from "@/lib/site/seo";

export function generateStaticParams() {
  return ARTICLES.map((a) => ({ slug: a.slug }));
}

export async function generateMetadata({ params }: { params: Promise<{ slug: string }> }) {
  const a = articleBySlug((await params).slug);
  if (!a) return {};
  return pageMeta({ title: a.title, description: a.excerpt, path: `/blog/${a.slug}`, type: "article" });
}

export default async function ArticlePage({ params }: { params: Promise<{ slug: string }> }) {
  const a = articleBySlug((await params).slug);
  if (!a) notFound();
  const related = ARTICLES.filter((x) => x.slug !== a.slug && x.category === a.category)
    .concat(ARTICLES.filter((x) => x.slug !== a.slug && x.category !== a.category))
    .slice(0, 3);
  const jsonLd = {
    "@context": "https://schema.org",
    "@type": "Article",
    headline: a.title,
    description: a.excerpt,
    datePublished: a.date,
    author: { "@type": "Organization", name: SITE.name },
    publisher: { "@type": "Organization", name: SITE.name, logo: { "@type": "ImageObject", url: `${SITE.url}/brand/icon-512.png` } },
    mainEntityOfPage: `${SITE.url}/blog/${a.slug}`,
  };
  return (
    <>
      <article>
        <header className="site-hero border-b border-brand-border">
          <Container className="max-w-[880px] pb-14 pt-10 md:pb-16 md:pt-14">
            <Breadcrumbs items={[{ label: "Blog", href: "/blog" }, { label: a.title }]} className="mb-8" />
            <Eyebrow>{a.category}</Eyebrow>
            <h1 className="mt-3 font-heading text-[2.2rem] font-semibold leading-[1.1] tracking-[-0.02em] text-brand-text md:text-[3rem]">{a.title}</h1>
            <p className="mt-5 text-[1.15rem] leading-relaxed text-brand-body">{a.excerpt}</p>
            <p className="mt-6 text-[0.9rem] text-brand-muted">
              By the BunonBrain team · <time dateTime={a.date}>{formatDate(a.date)}</time> · {a.minutes} min read
            </p>
          </Container>
        </header>
        <Container wide className="grid gap-12 py-14 lg:grid-cols-[minmax(0,1fr)_220px] lg:justify-center">
          <Prose blocks={a.body} className="mx-auto w-full max-w-[720px]" />
          <aside className="hidden lg:block">
            <div className="sticky top-28">
              <Toc blocks={a.body} />
            </div>
          </aside>
        </Container>
      </article>
      <Section tone="off" label="rel-title" className="py-16 md:py-20">
        <Container wide>
          <h2 id="rel-title" className="font-heading text-[1.5rem] font-semibold text-brand-text">
            Related reading
          </h2>
          <div className="mt-8 grid gap-5 md:grid-cols-3">
            {related.map((r) => (
              <ArticleCard key={r.slug} a={r} />
            ))}
          </div>
        </Container>
      </Section>
      <CtaBand />
      <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: JSON.stringify(jsonLd) }} />
    </>
  );
}
