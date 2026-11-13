"use client";

// Public site navigation (homepage). A clean white bar (light token scope)
// that gains a soft shadow once the page scrolls; highlights the section in view; signed-in visitors get
// "Open dashboard" instead of "Sign in"; a full menu on phones.

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { ArrowRight, LayoutGrid, LogIn, Menu, X } from "lucide-react";
import { LogoMark } from "@/components/SiteLogo";
import { refreshSession, useAuthStore } from "@/lib/auth";

const LINKS = [
  { href: "#tour", id: "tour", label: "Tour" },
  { href: "#how", id: "how", label: "How it works" },
  { href: "#demo", id: "demo", label: "Contact" },
];

function cn(...parts: Array<string | false | null | undefined>) {
  return parts.filter(Boolean).join(" ");
}

export function SiteNav() {
  const status = useAuthStore((s) => s.status);
  const user = useAuthStore((s) => s.user);
  const [scrolled, setScrolled] = useState(false);
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState<string | null>(null);
  const menuButton = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLDivElement>(null);

  // A returning visitor with a refresh cookie is recognised (no prompt, no redirect).
  useEffect(() => {
    if (useAuthStore.getState().status === "unknown") void refreshSession();
  }, []);

  useEffect(() => {
    const onScroll = () => setScrolled(window.scrollY > 12);
    onScroll();
    window.addEventListener("scroll", onScroll, { passive: true });
    return () => window.removeEventListener("scroll", onScroll);
  }, []);

  // Highlight the link for the section currently on screen.
  useEffect(() => {
    const targets = LINKS.map((l) => document.getElementById(l.id)).filter((el): el is HTMLElement => !!el);
    if (targets.length === 0) return;
    const io = new IntersectionObserver(
      (entries) => {
        const visible = entries.filter((e) => e.isIntersecting).sort((a, b) => b.intersectionRatio - a.intersectionRatio)[0];
        if (visible) setActive(visible.target.id);
      },
      { rootMargin: "-40% 0px -50% 0px", threshold: [0, 0.25, 0.5] }
    );
    targets.forEach((t) => io.observe(t));
    const atTop = () => window.scrollY < window.innerHeight * 0.5 && setActive(null);
    window.addEventListener("scroll", atTop, { passive: true });
    return () => {
      io.disconnect();
      window.removeEventListener("scroll", atTop);
    };
  }, []);

  // Phone menu: Escape closes it, focus moves in and back, the page doesn't scroll behind it.
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        setOpen(false);
        menuButton.current?.focus();
      }
    };
    document.addEventListener("keydown", onKey);
    const previous = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    panel.current?.querySelector<HTMLElement>("a")?.focus();
    return () => {
      document.removeEventListener("keydown", onKey);
      document.body.style.overflow = previous;
    };
  }, [open]);

  // Close the phone menu if the window grows to desktop width.
  useEffect(() => {
    const mq = window.matchMedia("(min-width: 768px)");
    const onChange = () => mq.matches && setOpen(false);
    mq.addEventListener("change", onChange);
    return () => mq.removeEventListener("change", onChange);
  }, []);

  const signedIn = status === "authenticated" && !!user;
  const firstName = user?.fullName?.trim().split(/\s+/)[0] || user?.email?.split("@")[0];

  return (
    <header
      className={cn(
        "fb-light fixed inset-x-0 top-0 z-40 border-b border-fb-line bg-fb-ink transition-shadow duration-200",
        (scrolled || open) && "shadow-[0_6px_20px_-12px_rgb(var(--brand-navy-deep)/0.35)]"
      )}
      data-testid="site-nav"
    >
      <div className="mx-auto flex h-16 max-w-[1440px] items-center gap-4 px-4 md:px-8">
        <a href="#top" className="inline-flex min-h-11 shrink-0 items-center gap-2.5" aria-label="BunonBrain, back to top" onClick={() => setOpen(false)}>
          <LogoMark />
          <span className="font-display text-[1.4rem] font-bold tracking-[0.02em]">BunonBrain</span>
        </a>

        <nav aria-label="Main" className="ml-6 hidden md:block">
          <ul className="flex items-center gap-1">
            {LINKS.map((l) => (
              <li key={l.id}>
                <a
                  href={l.href}
                  aria-current={active === l.id ? "location" : undefined}
                  className={cn(
                    "relative inline-flex min-h-11 items-center px-3 text-[0.92rem] transition-colors",
                    active === l.id ? "text-fb-text" : "text-fb-text/70 hover:text-fb-text"
                  )}
                >
                  {l.label}
                  <span
                    aria-hidden
                    className={cn(
                      "absolute inset-x-3 bottom-2 h-px origin-left bg-fb-lamp transition-transform duration-200",
                      active === l.id ? "scale-x-100" : "scale-x-0"
                    )}
                  />
                </a>
              </li>
            ))}
          </ul>
        </nav>

        <div className="ml-auto hidden items-center gap-2 md:flex">
          {signedIn ? (
            <Link href="/app" className="fb-btn fb-btn-solid" data-testid="nav-open-app">
              <LayoutGrid size={15} aria-hidden /> Open dashboard
              <span className="sr-only"> (signed in as {firstName})</span>
            </Link>
          ) : (
            <>
              <Link href="/login" className="inline-flex min-h-11 items-center gap-1.5 px-3 text-[0.92rem] text-fb-text/85 hover:text-fb-text" data-testid="nav-sign-in">
                <LogIn size={15} aria-hidden /> Sign in
              </Link>
              <a href="#demo" className="fb-btn fb-btn-solid">
                Book a demo <ArrowRight size={15} aria-hidden />
              </a>
            </>
          )}
        </div>

        <button
          ref={menuButton}
          type="button"
          className="ml-auto inline-flex size-11 items-center justify-center rounded-[3px] text-fb-text md:hidden"
          aria-expanded={open}
          aria-controls="site-menu"
          aria-label={open ? "Close menu" : "Open menu"}
          onClick={() => setOpen((o) => !o)}
          data-testid="nav-menu-button"
        >
          {open ? <X size={22} aria-hidden /> : <Menu size={22} aria-hidden />}
        </button>
      </div>

      {open && (
        <div id="site-menu" ref={panel} className="h-[calc(100svh-4rem)] overflow-y-auto border-t border-fb-line bg-fb-ink md:hidden">
          <nav aria-label="Main" className="px-4 py-4">
            <ul className="flex flex-col">
              {LINKS.map((l) => (
                <li key={l.id}>
                  <a
                    href={l.href}
                    onClick={() => setOpen(false)}
                    className="flex min-h-12 items-center justify-between border-b border-fb-line text-[1.05rem] text-fb-text"
                  >
                    {l.label} <ArrowRight size={16} aria-hidden className="text-fb-muted" />
                  </a>
                </li>
              ))}
            </ul>
          </nav>
          <div className="flex flex-col gap-3 px-4 pb-8 pt-2">
            {signedIn ? (
              <Link href="/app" className="fb-btn fb-btn-solid w-full">
                <LayoutGrid size={15} aria-hidden /> Open dashboard
              </Link>
            ) : (
              <>
                <Link href="/login" className="fb-btn fb-btn-ghost w-full" data-testid="nav-sign-in-mobile">
                  <LogIn size={15} aria-hidden /> Sign in
                </Link>
                <a href="#demo" onClick={() => setOpen(false)} className="fb-btn fb-btn-solid w-full">
                  Book a demo <ArrowRight size={15} aria-hidden />
                </a>
              </>
            )}
            {signedIn && <p className="text-center text-sm text-fb-muted">Signed in as {user?.email}</p>}
          </div>
        </div>
      )}
    </header>
  );
}
