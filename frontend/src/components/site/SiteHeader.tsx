"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useCallback, useEffect, useId, useRef, useState } from "react";
import { ArrowRight, ChevronDown, LogIn, Menu, X } from "lucide-react";
import { Logo } from "@/components/brand/Logo";
import { NAV, type NavMenu } from "@/content/site/navigation";
import { cn } from "@/lib/cn";

function isActive(menu: NavMenu, pathname: string) {
  if (menu.href) return menu.href === "/" ? pathname === "/" : pathname.startsWith(menu.href);
  return (menu.groups ?? []).some((g) => g.links.some((l) => l.href !== "/" && pathname.startsWith(l.href)));
}

function MegaPanel({ menu, id, onNavigate }: { menu: NavMenu; id: string; onNavigate: () => void }) {
  return (
    <div
      id={id}
      className="absolute left-1/2 top-full z-50 mt-3 w-[min(860px,calc(100vw-2rem))] -translate-x-1/2 rounded-2xl border border-brand-border bg-white p-3 shadow-[0_24px_60px_-20px_rgba(16,38,61,0.35)]"
    >
      <div className={cn("grid gap-2", menu.feature ? "md:grid-cols-[1fr_1fr_15rem]" : "md:grid-cols-2")}>
        {menu.groups?.map((g) => (
          <div key={g.title} className="p-3">
            <p className="px-2 font-plexmono text-[0.68rem] uppercase tracking-[0.14em] text-brand-muted">{g.title}</p>
            <ul className="mt-2 space-y-0.5">
              {g.links.map((l) => (
                <li key={l.href}>
                  <Link href={l.href} onClick={onNavigate} className="block rounded-lg px-2 py-2 hover:bg-brand-offwhite focus-visible:bg-brand-offwhite">
                    <span className="block text-[0.93rem] font-medium text-brand-text">{l.label}</span>
                    {l.description && <span className="mt-0.5 block text-[0.82rem] leading-snug text-brand-muted">{l.description}</span>}
                  </Link>
                </li>
              ))}
            </ul>
          </div>
        ))}
        {menu.feature && (
          <Link
            href={menu.feature.href}
            onClick={onNavigate}
            className="site-cta group relative flex flex-col justify-between overflow-hidden rounded-xl p-5 text-white"
          >
            <div aria-hidden className="site-weave pointer-events-none absolute inset-0" />
            <div className="relative">
              <p className="font-plexmono text-[0.66rem] uppercase tracking-[0.14em] text-[rgb(var(--brand-emerald-on-dark))]">{menu.feature.eyebrow}</p>
              <p className="mt-2 font-heading text-[1.2rem] font-semibold leading-snug">{menu.feature.title}</p>
              <p className="mt-2 text-[0.85rem] leading-relaxed text-white/75">{menu.feature.body}</p>
            </div>
            <span className="relative mt-6 inline-flex items-center gap-1.5 text-[0.88rem] font-medium">
              {menu.feature.cta} <ArrowRight size={15} aria-hidden className="transition-transform group-hover:translate-x-0.5" />
            </span>
          </Link>
        )}
      </div>
    </div>
  );
}

export function SiteHeader() {
  const pathname = usePathname() ?? "/";
  const [open, setOpen] = useState<string | null>(null);
  const [mobile, setMobile] = useState(false);
  const [mobileGroup, setMobileGroup] = useState<string | null>(null);
  const [scrolled, setScrolled] = useState(false);
  const navRef = useRef<HTMLDivElement>(null);
  const closeTimer = useRef<number | undefined>(undefined);
  const baseId = useId();

  const close = useCallback(() => {
    setOpen(null);
    setMobile(false);
  }, []);

  useEffect(() => {
    const onScroll = () => setScrolled(window.scrollY > 8);
    onScroll();
    window.addEventListener("scroll", onScroll, { passive: true });
    return () => window.removeEventListener("scroll", onScroll);
  }, []);

  // Close on route change, Escape and outside click.
  useEffect(() => close(), [pathname, close]);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && close();
    const onDown = (e: PointerEvent) => {
      if (navRef.current && !navRef.current.contains(e.target as Node)) setOpen(null);
    };
    document.addEventListener("keydown", onKey);
    document.addEventListener("pointerdown", onDown);
    return () => {
      document.removeEventListener("keydown", onKey);
      document.removeEventListener("pointerdown", onDown);
    };
  }, [close]);

  // Lock page scroll behind the mobile menu.
  useEffect(() => {
    document.documentElement.style.overflow = mobile ? "hidden" : "";
    return () => {
      document.documentElement.style.overflow = "";
    };
  }, [mobile]);

  const hoverOpen = (label: string) => {
    window.clearTimeout(closeTimer.current);
    setOpen(label);
  };
  const hoverClose = () => {
    closeTimer.current = window.setTimeout(() => setOpen(null), 140);
  };

  return (
    <header
      className={cn(
        "sticky top-0 z-40 border-b transition-[background-color,border-color,box-shadow] duration-200",
        scrolled || mobile ? "border-brand-border bg-white/95 shadow-[0_1px_0_rgba(16,38,61,0.02)] backdrop-blur" : "border-transparent bg-white/70 backdrop-blur-sm",
      )}
    >
      <div ref={navRef} className="mx-auto flex h-[72px] max-w-[1320px] items-center gap-6 px-4 sm:px-6 lg:px-8">
        <Link href="/" className="inline-flex min-h-11 shrink-0 items-center" aria-label="BunonBrain home">
          <Logo height={32} />
        </Link>

        <nav aria-label="Main" className="ml-4 hidden lg:block">
          <ul className="flex items-center gap-0.5">
            {NAV.map((menu) => {
              const active = isActive(menu, pathname);
              const panelId = `${baseId}-${menu.label}`;
              if (menu.href) {
                return (
                  <li key={menu.label}>
                    <Link
                      href={menu.href}
                      aria-current={active ? "page" : undefined}
                      className={cn("inline-flex min-h-11 items-center rounded-lg px-3 text-[0.93rem] font-medium transition-colors", active ? "text-brand-text" : "text-brand-body hover:text-brand-text")}
                    >
                      {menu.label}
                    </Link>
                  </li>
                );
              }
              const isOpen = open === menu.label;
              return (
                <li key={menu.label} className="relative" onMouseEnter={() => hoverOpen(menu.label)} onMouseLeave={hoverClose}>
                  <button
                    type="button"
                    aria-expanded={isOpen}
                    aria-controls={panelId}
                    onClick={() => setOpen(isOpen ? null : menu.label)}
                    className={cn(
                      "inline-flex min-h-11 items-center gap-1 rounded-lg px-3 text-[0.93rem] font-medium transition-colors",
                      active || isOpen ? "text-brand-text" : "text-brand-body hover:text-brand-text",
                    )}
                  >
                    {menu.label}
                    <ChevronDown size={15} aria-hidden className={cn("transition-transform", isOpen && "rotate-180")} />
                  </button>
                  {isOpen && <MegaPanel menu={menu} id={panelId} onNavigate={close} />}
                </li>
              );
            })}
          </ul>
        </nav>

        <div className="ml-auto hidden items-center gap-2 lg:flex">
          <Link href="/login" className="inline-flex min-h-11 items-center gap-1.5 rounded-lg px-3 text-[0.93rem] font-medium text-brand-body hover:text-brand-text">
            <LogIn size={16} aria-hidden /> Sign in
          </Link>
          <Link
            href="/demo"
            className="inline-flex min-h-11 items-center gap-1.5 rounded-lg bg-brand-emerald-ink px-5 text-[0.93rem] font-medium text-white hover:bg-[rgb(var(--brand-emerald-ink-hover))]"
          >
            Request a demo <ArrowRight size={15} aria-hidden />
          </Link>
        </div>

        <button
          type="button"
          className="ml-auto inline-flex size-11 items-center justify-center rounded-lg text-brand-text lg:hidden"
          aria-expanded={mobile}
          aria-controls={`${baseId}-mobile`}
          aria-label={mobile ? "Close menu" : "Open menu"}
          onClick={() => setMobile((v) => !v)}
        >
          {mobile ? <X size={22} aria-hidden /> : <Menu size={22} aria-hidden />}
        </button>
      </div>

      {mobile && (
        <div id={`${baseId}-mobile`} className="h-[calc(100dvh-72px)] overflow-y-auto border-t border-brand-border bg-white lg:hidden">
          <nav aria-label="Main" className="px-4 pb-10 pt-2 sm:px-6">
            <ul>
              {NAV.map((menu) =>
                menu.href ? (
                  <li key={menu.label} className="border-b border-brand-border">
                    <Link href={menu.href} onClick={close} className="flex min-h-14 items-center text-[1.05rem] font-medium text-brand-text">
                      {menu.label}
                    </Link>
                  </li>
                ) : (
                  <li key={menu.label} className="border-b border-brand-border">
                    <button
                      type="button"
                      aria-expanded={mobileGroup === menu.label}
                      onClick={() => setMobileGroup(mobileGroup === menu.label ? null : menu.label)}
                      className="flex min-h-14 w-full items-center justify-between text-[1.05rem] font-medium text-brand-text"
                    >
                      {menu.label}
                      <ChevronDown size={18} aria-hidden className={cn("transition-transform", mobileGroup === menu.label && "rotate-180")} />
                    </button>
                    {mobileGroup === menu.label && (
                      <div className="pb-4">
                        {menu.groups?.map((g) => (
                          <div key={g.title} className="mt-2">
                            <p className="font-plexmono text-[0.66rem] uppercase tracking-[0.14em] text-brand-muted">{g.title}</p>
                            <ul className="mt-1">
                              {g.links.map((l) => (
                                <li key={l.href}>
                                  <Link href={l.href} onClick={close} className="flex min-h-11 items-center text-[0.98rem] text-brand-body">
                                    {l.label}
                                  </Link>
                                </li>
                              ))}
                            </ul>
                          </div>
                        ))}
                      </div>
                    )}
                  </li>
                ),
              )}
            </ul>
            <div className="mt-8 grid gap-3">
              <Link href="/demo" onClick={close} className="inline-flex min-h-12 items-center justify-center gap-2 rounded-lg bg-brand-emerald-ink font-medium text-white">
                Request a demo <ArrowRight size={16} aria-hidden />
              </Link>
              <Link href="/login" onClick={close} className="inline-flex min-h-12 items-center justify-center gap-2 rounded-lg border border-brand-border font-medium text-brand-text">
                <LogIn size={16} aria-hidden /> Sign in
              </Link>
            </div>
          </nav>
        </div>
      )}
    </header>
  );
}
