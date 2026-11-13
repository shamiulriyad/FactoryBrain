import Link from "next/link";
import { ArrowRight } from "lucide-react";
import { Logo } from "@/components/brand/Logo";
import { FOOTER } from "@/content/site/navigation";
import { SITE } from "@/lib/site/config";
import { Container } from "./ui";

export function SiteFooter() {
  const year = new Date().getFullYear();
  const { email, phone } = SITE.contact;
  return (
    <footer className="site-on-dark relative overflow-hidden text-white" aria-labelledby="site-footer-title">
      <h2 id="site-footer-title" className="sr-only">
        Site footer
      </h2>
      <div aria-hidden className="site-weave pointer-events-none absolute inset-x-0 top-0 h-64 opacity-70" />
      <Container wide className="relative pb-10 pt-16 md:pt-20">
        <div className="grid gap-12 lg:grid-cols-[minmax(0,1.3fr)_minmax(0,3fr)]">
          <div className="max-w-sm">
            <Logo height={36} tone="onDark" label="BunonBrain" />
            <p className="mt-4 font-plexmono text-[0.72rem] uppercase tracking-[0.14em] text-[rgb(var(--brand-emerald-on-dark))]">{SITE.tagline}</p>
            <p className="mt-4 text-[0.95rem] leading-relaxed text-white/70">
              The operations layer for Bangladesh&apos;s ready-made garment factories: production, quality, merchandising and
              workforce in one role-based system.
            </p>
            <Link
              href="/demo"
              className="mt-7 inline-flex min-h-11 items-center gap-2 rounded-lg bg-brand-emerald-ink px-5 text-[0.93rem] font-medium hover:bg-[rgb(var(--brand-emerald-ink-hover))]"
            >
              Request a demo <ArrowRight size={15} aria-hidden />
            </Link>
          </div>

          <div className="grid grid-cols-2 gap-10 sm:grid-cols-3 lg:grid-cols-5">
            {FOOTER.map((g) => (
              <nav key={g.title} aria-label={g.title}>
                <h3 className="font-plexmono text-[0.7rem] uppercase tracking-[0.14em] text-white/50">{g.title}</h3>
                <ul className="mt-4 space-y-1">
                  {g.links.map((l) => (
                    <li key={l.href}>
                      <Link href={l.href} className="inline-flex min-h-9 items-center text-[0.93rem] text-white/80 hover:text-white">
                        {l.label}
                      </Link>
                    </li>
                  ))}
                </ul>
              </nav>
            ))}
            <div>
              <h3 className="font-plexmono text-[0.7rem] uppercase tracking-[0.14em] text-white/50">Connect</h3>
              <ul className="mt-4 space-y-1 text-[0.93rem]">
                {email && (
                  <li>
                    <a href={`mailto:${email}`} className="inline-flex min-h-9 items-center text-white/80 hover:text-white">
                      {email}
                    </a>
                  </li>
                )}
                {phone && (
                  <li>
                    <a href={`tel:${phone.replace(/\s/g, "")}`} className="inline-flex min-h-9 items-center text-white/80 hover:text-white">
                      {phone}
                    </a>
                  </li>
                )}
                {SITE.social.map((s) => (
                  <li key={s.href}>
                    <a href={s.href} rel="me noopener" target="_blank" className="inline-flex min-h-9 items-center text-white/80 hover:text-white">
                      {s.label}
                    </a>
                  </li>
                ))}
                <li>
                  <Link href="/contact" className="inline-flex min-h-9 items-center text-white/80 hover:text-white">
                    Contact form
                  </Link>
                </li>
                <li>
                  <Link href="/support" className="inline-flex min-h-9 items-center text-white/80 hover:text-white">
                    Support
                  </Link>
                </li>
                <li>
                  <Link href="/feedback" className="inline-flex min-h-9 items-center text-white/80 hover:text-white">
                    Send feedback
                  </Link>
                </li>
              </ul>
            </div>
          </div>
        </div>

        <div className="mt-16 flex flex-col gap-3 border-t border-white/10 pt-6 text-[0.84rem] text-white/55 md:flex-row md:items-center md:justify-between">
          <p>
            © {year} {SITE.legal.entityName ?? "BunonBrain"}. All rights reserved.
          </p>
          <ul className="flex flex-wrap gap-x-5 gap-y-1">
            <li>
              <Link href="/legal/privacy-policy" className="hover:text-white">
                Privacy
              </Link>
            </li>
            <li>
              <Link href="/legal/terms-of-service" className="hover:text-white">
                Terms
              </Link>
            </li>
            <li>
              <Link href="/legal/cookie-policy" className="hover:text-white">
                Cookies
              </Link>
            </li>
            <li>
              <Link href="/legal/accessibility-statement" className="hover:text-white">
                Accessibility
              </Link>
            </li>
            <li>
              <Link href="/sitemap.xml" className="hover:text-white">
                Sitemap
              </Link>
            </li>
          </ul>
        </div>
      </Container>
    </footer>
  );
}
