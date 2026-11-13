import type { Metadata, Viewport } from "next";
import type { ReactNode } from "react";
import { SiteFooter } from "@/components/site/SiteFooter";
import { SiteHeader } from "@/components/site/SiteHeader";
import { siteFontVariables } from "@/lib/fonts";
import { SITE } from "@/lib/site/config";

/**
 * Public corporate website. Kept apart from the signed-in application
 * (app/app) and the auth pages: its own header, footer, fonts and tokens.
 */
export const metadata: Metadata = {
  metadataBase: new URL(SITE.url),
  title: { default: `${SITE.name}: ${SITE.category}`, template: `%s | ${SITE.name}` },
  description: SITE.description,
  applicationName: SITE.name,
  openGraph: { siteName: SITE.name, type: "website", locale: "en_US" },
  twitter: { card: "summary_large_image" },
};

export const viewport: Viewport = { themeColor: "#FFFFFF", colorScheme: "light" };

export default function SiteLayout({ children }: { children: ReactNode }) {
  const orgJsonLd = {
    "@context": "https://schema.org",
    "@type": "Organization",
    name: SITE.name,
    url: SITE.url,
    logo: `${SITE.url}/brand/icon-512.png`,
    slogan: SITE.tagline,
    description: SITE.description,
    ...(SITE.social.length ? { sameAs: SITE.social.map((s) => s.href) } : {}),
  };
  return (
    <div className={`${siteFontVariables} site-root flex min-h-[100dvh] flex-col`}>
      <a
        href="#main"
        className="sr-only z-[60] rounded-lg bg-brand-navy px-4 py-2 text-white focus:not-sr-only focus:fixed focus:left-4 focus:top-4"
      >
        Skip to content
      </a>
      <SiteHeader />
      <main id="main" className="flex-1">
        {children}
      </main>
      <SiteFooter />
      <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: JSON.stringify(orgJsonLd) }} />
    </div>
  );
}
