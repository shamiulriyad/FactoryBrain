import Link from "next/link";
import { EXAMPLE_CONTENT } from "@/lib/tour";

/** FactoryBrain mark: a building section with one lit floor. */
export function LogoMark({ size = 26 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 26 26" aria-hidden="true" focusable="false">
      <rect x="3.5" y="2.5" width="19" height="21" rx="1.5" fill="none" stroke="#ece7df" strokeWidth="1.6" />
      <path d="M3.5 8.2h19M3.5 13h19M3.5 17.8h19" stroke="#ece7df" strokeWidth="1.2" opacity="0.55" />
      <rect x="5.2" y="9.4" width="15.6" height="2.4" fill="#f2c27a" />
      <circle cx="16.5" cy="10.6" r="1.5" fill="#ff6b45" />
    </svg>
  );
}

const navLink =
  "hidden min-h-11 items-center px-3 text-[0.92rem] text-fb-text/85 transition-colors hover:text-fb-text sm:inline-flex";

export function SiteNav() {
  return (
    <header className="fixed inset-x-0 top-0 z-40 border-b border-fb-line bg-fb-card backdrop-blur-md">
      <div className="mx-auto flex h-16 max-w-[1440px] items-center justify-between gap-4 px-4 md:px-8">
        <a href="#top" className="inline-flex min-h-11 items-center gap-2.5" aria-label="FactoryBrain, back to top">
          <LogoMark />
          <span className="font-display text-[1.45rem] font-bold tracking-[0.02em]">FactoryBrain</span>
        </a>
        <nav aria-label="Main">
          <ul className="flex items-center gap-1 md:gap-2">
            <li>
              <a href="#tour" className={navLink}>
                Tour
              </a>
            </li>
            <li>
              <a href="#how" className={navLink}>
                How it works
              </a>
            </li>
            <li className="ml-1">
              <a href="#demo" className="fb-btn fb-btn-solid">
                Book a demo
              </a>
            </li>
          </ul>
        </nav>
      </div>
    </header>
  );
}

export function SiteFooter() {
  return (
    <footer className="relative z-20 border-t border-fb-line bg-fb-ink">
      <div className="mx-auto flex max-w-[1200px] flex-col gap-8 px-4 py-12 md:flex-row md:items-start md:justify-between md:px-8">
        <div className="max-w-sm">
          <div className="flex items-center gap-2.5">
            <LogoMark size={22} />
            <span className="font-display text-xl font-bold">FactoryBrain</span>
          </div>
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
          </ul>
        </nav>
      </div>
      <div className="border-t border-fb-line">
        <div className="mx-auto flex max-w-[1200px] flex-col gap-2 px-4 py-5 font-plexmono text-[0.75rem] text-fb-muted md:flex-row md:justify-between md:px-8">
          <p>© {new Date().getFullYear()} FactoryBrain</p>
          {EXAMPLE_CONTENT && (
            <p>Figures on this page are illustrative examples, not readings from a real factory.</p>
          )}
        </div>
      </div>
    </footer>
  );
}
