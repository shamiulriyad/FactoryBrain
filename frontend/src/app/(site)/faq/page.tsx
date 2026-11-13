import { Container, CtaBand, PageHero, Section } from "@/components/site/ui";
import { FaqList } from "@/components/site/visuals";
import { FAQS, type Faq } from "@/content/site/faqs";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Frequently asked questions",
  description: "Answers about BunonBrain: what it does, what is available today, data and security, AI and getting started.",
  path: "/faq",
});

const CATS: Faq["category"][] = ["Product", "Getting started", "AI", "Data & security"];

export default function FaqPage() {
  const jsonLd = {
    "@context": "https://schema.org",
    "@type": "FAQPage",
    mainEntity: FAQS.map((f) => ({ "@type": "Question", name: f.q, acceptedAnswer: { "@type": "Answer", text: f.a } })),
  };
  return (
    <>
      <PageHero crumbs={[{ label: "Resources", href: "/resources" }, { label: "FAQs" }]} eyebrow="FAQs" title="Frequently asked questions" lead="Short, direct answers. If yours isn't here, contact us." />
      <Section>
        <Container className="max-w-[900px] space-y-14">
          {CATS.map((c) => (
            <section key={c} aria-labelledby={`faq-${c}`}>
              <h2 id={`faq-${c}`} className="mb-5 font-heading text-[1.5rem] font-semibold text-brand-text">
                {c}
              </h2>
              <FaqList items={FAQS.filter((f) => f.category === c).map((f) => ({ q: f.q, a: f.a }))} />
            </section>
          ))}
        </Container>
      </Section>
      <CtaBand title="Still have a question?" body="Send it to our team and we'll answer it directly." primary={{ label: "Contact us", href: "/contact" }} secondary={{ label: "Help center", href: "/help" }} />
      <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: JSON.stringify(jsonLd) }} />
    </>
  );
}
