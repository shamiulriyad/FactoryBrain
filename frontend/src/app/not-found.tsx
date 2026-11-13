import Link from "next/link";
import { Logo } from "@/components/brand/Logo";
import { siteFontVariables } from "@/lib/fonts";

export const metadata = { title: "Page not found | BunonBrain", robots: { index: false } };

const LINKS = [
  ["Home", "/"],
  ["Product", "/product"],
  ["Solutions", "/solutions"],
  ["Resources", "/resources"],
  ["Help center", "/help"],
  ["Contact", "/contact"],
];

/** 404 for every route (public site and application). */
export default function NotFound() {
  return (
    <div className={`${siteFontVariables} site-root site-hero flex min-h-[100dvh] flex-col`}>
      <header className="mx-auto flex h-[72px] w-full max-w-[1200px] items-center px-4 sm:px-6 lg:px-8">
        <Link href="/" aria-label="BunonBrain home" className="inline-flex min-h-11 items-center">
          <Logo height={32} />
        </Link>
      </header>
      <main id="main" className="mx-auto flex w-full max-w-[1200px] flex-1 flex-col justify-center px-4 py-16 sm:px-6 lg:px-8">
        <p className="font-plexmono text-[0.8rem] uppercase tracking-[0.16em] text-brand-emerald-ink">Error 404</p>
        <h1 className="mt-3 max-w-[40rem] font-heading text-[2.6rem] font-semibold leading-[1.08] tracking-[-0.025em] text-brand-text md:text-[3.4rem]">
          This page has gone off the line.
        </h1>
        <p className="mt-5 max-w-[36rem] text-[1.1rem] leading-relaxed text-brand-body">
          The address may be mistyped, or the page may have moved. Try one of these instead.
        </p>
        <ul className="mt-9 flex flex-wrap gap-3">
          {LINKS.map(([label, href]) => (
            <li key={href}>
              <Link
                href={href}
                className={
                  href === "/"
                    ? "inline-flex min-h-11 items-center rounded-lg bg-brand-emerald-ink px-5 font-medium text-white hover:bg-[rgb(var(--brand-emerald-ink-hover))]"
                    : "inline-flex min-h-11 items-center rounded-lg border border-brand-border bg-white px-5 font-medium text-brand-text hover:border-brand-navy/30"
                }
              >
                {label}
              </Link>
            </li>
          ))}
        </ul>
      </main>
    </div>
  );
}
