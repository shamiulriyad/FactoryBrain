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

export const metadata: Metadata = {
  title: "FactoryBrain: your whole garment factory, floor by floor",
  description:
    "FactoryBrain uses cameras, machine data and analytics to catch problems in fabric inspection, cutting, sewing, QC, inventory and shipment.",
  applicationName: "FactoryBrain",
};

export const viewport: Viewport = {
  themeColor: "#0f141b",
  colorScheme: "dark",
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
