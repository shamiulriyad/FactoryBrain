import Link from "next/link";
import { EXAMPLE_CONTENT } from "@/lib/tour";

import { Logo } from "@/components/brand/Logo";
import { TAGLINE } from "@/components/brand/logoPaths";

export { LogoMark } from "@/components/SiteLogo";

/** The site navigation lives in its own client component (scroll state, phone menu, session). */
export { SiteNav } from "@/components/SiteNav";

export function SiteFooter() {
  return (
    <footer className="fb-dark relative z-20 border-t border-fb-line bg-fb-ink">
      <div className="mx-auto flex max-w-[1200px] flex-col gap-8 px-4 py-12 md:flex-row md:items-start md:justify-between md:px-8">
        <div className="max-w-sm">
          <Logo height={34} tone="onDark" label="BunonBrain" />
          <p className="mt-3 font-plexmono text-[0.72rem] uppercase tracking-[0.14em] text-fb-lamp">{TAGLINE}</p>
          <p className="mt-3 text-sm text-fb-muted">
            AI operations for garment factories: fabric, cutting, sewing, QC and shipment in one view.
          </p>
        </div>
        <nav aria-label="Footer">
          <ul className="grid grid-cols-2 gap-x-8 text-sm">
            <li>
              <a href="#tour" className="inline-flex min-h-11 items-center text-fb-text/85 hover:text-fb-text">
                Tour
              </a>
            </li>
            <li>
              <a href="#how" className="inline-flex min-h-11 items-center text-fb-text/85 hover:text-fb-text">
                How it works
              </a>
            </li>
            <li>
              <a href="#demo" className="inline-flex min-h-11 items-center text-fb-text/85 hover:text-fb-text">
                Book a demo
              </a>
            </li>
            <li>
              <Link href="/login" className="inline-flex min-h-11 items-center text-fb-text/85 hover:text-fb-text">
                Sign in
              </Link>
            </li>
            <li>
              <Link href="/brand" className="inline-flex min-h-11 items-center text-fb-text/85 hover:text-fb-text">
                Brand
              </Link>
            </li>
          </ul>
        </nav>
      </div>
      <div className="border-t border-fb-line">
        <div className="mx-auto flex max-w-[1200px] flex-col gap-2 px-4 py-5 font-plexmono text-[0.75rem] text-fb-muted md:flex-row md:justify-between md:px-8">
          <p>© {new Date().getFullYear()} BunonBrain</p>
          {EXAMPLE_CONTENT && (
            <p>Figures on this page are illustrative examples, not readings from a real factory.</p>
          )}
        </div>
      </div>
    </footer>
  );
}
