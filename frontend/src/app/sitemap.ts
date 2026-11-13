import type { MetadataRoute } from "next";
import { ARTICLES } from "@/content/site/articles";
import { DOCS } from "@/content/site/docs";
import { LEGAL } from "@/content/site/legal";
import { MODULES } from "@/content/site/modules";
import { SOLUTIONS } from "@/content/site/solutions";
import { SITE } from "@/lib/site/config";

/** Public pages only; the signed-in app, auth and API routes are excluded. */
const STATIC = [
  "/", "/about", "/about/mission", "/about/story", "/solutions", "/product", "/product/features", "/how-it-works",
  "/industries", "/why-bunonbrain", "/contact", "/demo", "/consultation",
  "/company", "/company/values", "/company/culture", "/company/leadership", "/careers", "/news", "/press", "/partners", "/brand",
  "/resources", "/blog", "/insights", "/guides", "/case-studies", "/faq", "/docs", "/help",
  "/support", "/support/report-a-problem", "/support/feature-request", "/feedback", "/status", "/legal",
];

export default function sitemap(): MetadataRoute.Sitemap {
  const now = new Date();
  const entry = (path: string, priority = 0.6): MetadataRoute.Sitemap[number] => ({
    url: `${SITE.url}${path}`,
    lastModified: now,
    changeFrequency: "monthly",
    priority,
  });
  return [
    ...STATIC.map((p) => entry(p, p === "/" ? 1 : ["/product", "/solutions", "/demo", "/about"].includes(p) ? 0.9 : 0.6)),
    ...SOLUTIONS.map((s) => entry(`/solutions/${s.slug}`, 0.8)),
    ...MODULES.map((m) => entry(`/product/${m.slug}`, 0.8)),
    ...ARTICLES.map((a) => ({ ...entry(`/blog/${a.slug}`, 0.6), lastModified: new Date(a.date) })),
    ...DOCS.map((d) => entry(`/docs/${d.slug}`, 0.5)),
    ...LEGAL.map((l) => ({ ...entry(`/legal/${l.slug}`, 0.3), lastModified: new Date(l.updated) })),
  ];
}
