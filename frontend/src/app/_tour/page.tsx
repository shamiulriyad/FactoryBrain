import type { Metadata, Viewport } from "next";
import { DemoSection } from "@/components/DemoForm";
import { FloorRail } from "@/components/FloorRail";
import { Hero } from "@/components/Hero";
import { HowItWorks } from "@/components/HowItWorks";
import { SiteFooter, SiteNav } from "@/components/SiteChrome";
import { TourBackdrop } from "@/components/TourBackdrop";
import { TourStep } from "@/components/TourCard";
import { TOUR_SECTIONS } from "@/lib/tour";
import { fontVariables } from "@/lib/fonts";
import { pageMeta } from "@/lib/site/seo";
import { SITE } from "@/lib/site/config";

/*
 * 3D factory tour. Not routed at the moment: the folder is named "_tour", and
 * Next.js ignores folders starting with "_". Rename it back to "tour" (and
 * re-add links from the homepage, navigation and sitemap) to publish it again.
 */
export const metadata: Metadata = {
  ...pageMeta({
    title: "3D factory tour",
    description:
      "Fly through a garment factory floor by floor and see where BunonBrain watches fabric inspection, cutting, sewing lines, QC and packing.",
    path: "/tour",
  }),
  metadataBase: new URL(SITE.url),
};

export const viewport: Viewport = {
  themeColor: "#ffffff",
  colorScheme: "light",
};

export default function HomePage() {
  return (
    <div className={`fb-home ${fontVariables} relative`}>
      <a
        href="#main"
        className="sr-only z-50 rounded-[3px] bg-fb-text px-3 py-2 text-fb-ink focus:not-sr-only focus:fixed focus:left-4 focus:top-4"
      >
        Skip to content
      </a>
      <SiteNav />
      <TourBackdrop />
      <FloorRail />
      <main id="main">
        <Hero />
        <div id="tour">
          {TOUR_SECTIONS.map((section, i) => (
            <TourStep key={section.step} section={section} last={i === TOUR_SECTIONS.length - 1} />
          ))}
        </div>
        <HowItWorks />
        <DemoSection />
      </main>
      <SiteFooter />
    </div>
  );
}
