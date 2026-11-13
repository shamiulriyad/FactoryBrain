import type { ResourceItem } from "@/components/site/ResourceExplorer";
import { ARTICLES } from "./articles";
import { DOCS } from "./docs";
import { FAQS } from "./faqs";
import { slugify } from "./blocks";

/** Every searchable resource on the site. */
export function allResources(): ResourceItem[] {
  return [
    ...ARTICLES.map((a) => ({ title: a.title, href: `/blog/${a.slug}`, type: a.category === "Guides" ? "Guide" : a.category === "RMG insights" ? "Insight" : "Article", summary: a.excerpt })),
    ...DOCS.map((d) => ({ title: d.title, href: `/docs/${d.slug}`, type: "Documentation", summary: d.summary })),
    ...FAQS.map((f) => ({ title: f.q, href: `/faq#${slugify(f.q)}`, type: "FAQ", summary: f.a.length > 150 ? `${f.a.slice(0, 147)}…` : f.a })),
  ];
}
