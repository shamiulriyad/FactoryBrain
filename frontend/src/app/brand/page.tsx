import type { Metadata, Viewport } from "next";
import Link from "next/link";
import type { ReactNode } from "react";
import { Logo, LogoAppIcon, LogoSymbol, Wordmark, type LogoTone } from "@/components/brand/Logo";
import { LOGO_COLORS, TAGLINE } from "@/components/brand/logoPaths";
import { plexMono, plexSans, wordmarkFont } from "@/lib/fonts";

export const metadata: Metadata = {
  title: "BunonBrain brand identity",
  description: "The BunonBrain logo, symbol, colours and typography, with downloadable files.",
};

export const viewport: Viewport = { themeColor: "#FFFFFF", colorScheme: "light" };

const PALETTE = [
  { name: "Primary Navy", hex: LOGO_COLORS.navy, rgb: "22 50 79", role: "Dominant. Symbol, “Bunon”, headings.", dark: true },
  { name: "Deep Navy", hex: LOGO_COLORS.navyDeep, rgb: "16 38 61", role: "Dark backgrounds, sidebar, footer.", dark: true },
  { name: "Emerald Green", hex: LOGO_COLORS.emerald, rgb: "5 150 105", role: "Accent. Fabric waves, “Brain”, actions.", dark: true },
  { name: "Light Green", hex: LOGO_COLORS.lightGreen, rgb: "209 250 229", role: "Soft fills, highlights, badges.", dark: false },
  { name: "White", hex: LOGO_COLORS.white, rgb: "255 255 255", role: "Page ground and reversed logo.", dark: false },
] as const;

const DOWNLOADS = [
  { file: "bunonbrain-logo.svg", label: "Primary logo", note: "SVG, for white and light backgrounds" },
  { file: "bunonbrain-logo-on-dark.svg", label: "Logo on dark", note: "SVG, for navy and photo backgrounds" },
  { file: "bunonbrain-logo-mono-navy.svg", label: "Monochrome navy", note: "SVG, one-colour print" },
  { file: "bunonbrain-logo-mono-black.svg", label: "Monochrome black", note: "SVG, fax, stamps, laser engraving" },
  { file: "bunonbrain-logo-mono-white.svg", label: "Monochrome white", note: "SVG, reversed out of any colour" },
  { file: "bunonbrain-symbol.svg", label: "Symbol", note: "SVG, standalone mark" },
  { file: "bunonbrain-symbol-on-dark.svg", label: "Symbol on dark", note: "SVG" },
  { file: "bunonbrain-app-icon.svg", label: "App icon", note: "SVG, rounded navy tile" },
  { file: "icon-512.png", label: "App icon 512 px", note: "PNG, app stores and PWA" },
  { file: "icon-maskable-512.png", label: "Maskable icon 512 px", note: "PNG, Android adaptive icon" },
] as const;

function Section({ id, eyebrow, title, children, intro }: { id: string; eyebrow: string; title: string; intro?: string; children: ReactNode }) {
  return (
    <section id={id} aria-labelledby={`${id}-title`} className="border-t border-[#E2E8F0] py-16 md:py-20">
      <p className="font-plexmono text-[0.72rem] uppercase tracking-[0.16em] text-[#64748B]">{eyebrow}</p>
      <h2 id={`${id}-title`} className="mt-2 font-[family-name:var(--font-wordmark)] text-[2rem] font-bold leading-tight tracking-[-0.01em] md:text-[2.4rem]">
        {title}
      </h2>
      {intro && <p className="mt-3 max-w-[60ch] text-[1rem] leading-relaxed text-[#334155]">{intro}</p>}
      <div className="mt-10">{children}</div>
    </section>
  );
}

function Tile({ label, bg, border = false, children, className = "" }: { label: string; bg: string; border?: boolean; children: ReactNode; className?: string }) {
  return (
    <figure className={`flex flex-col overflow-hidden rounded-xl ${border ? "border border-[#E2E8F0]" : ""} ${className}`}>
      <div className="flex min-h-[180px] flex-1 items-center justify-center p-8" style={{ background: bg }}>
        {children}
      </div>
      <figcaption className="border-t border-[#E2E8F0] bg-white px-4 py-3 font-plexmono text-[0.74rem] text-[#334155]">{label}</figcaption>
    </figure>
  );
}

function LogoTile({ label, bg, tone, border, mono }: { label: string; bg: string; tone: LogoTone; border?: boolean; mono?: string }) {
  return (
    <Tile label={label} bg={bg} border={border}>
      <span style={mono ? { color: mono } : undefined} className="max-w-full">
        <Logo height={48} tone={tone} label={`BunonBrain logo, ${label}`} className="h-auto max-w-full" />
      </span>
    </Tile>
  );
}

/** The symbol, large, with the three ideas it carries called out. */
function SymbolAnatomy() {
  const notes = [
    { n: "1", title: "Garment hanger", body: "The hook and sloping shoulders form the B's upper bowl: a precise, engineered hanger, not a shop-window one. They also read as the roof line of a factory." },
    { n: "2", title: "The letter B", body: "One continuous stem and two bowls in the same weight carry the structure: the workforce and the operations that hold a plant together." },
    { n: "3", title: "Fabric and thread", body: "Emerald waves in the lower bowl are fabric on the move and threads running through it: the material, and the data the system reads from it." },
  ];
  return (
    <div className="grid items-center gap-10 md:grid-cols-[minmax(0,1fr)_minmax(0,1.1fr)] md:gap-14">
      <div className="relative mx-auto aspect-square w-full max-w-[360px] rounded-2xl border border-[#E2E8F0] bg-white">
        {/* 8-unit construction grid behind the symbol. */}
        <svg viewBox="4 2 40 40" className="absolute inset-0 h-full w-full" aria-hidden="true">
          {Array.from({ length: 11 }, (_, i) => (
            <g key={i} stroke="#E2E8F0" strokeWidth="0.12">
              <line x1={4 + i * 4} y1="2" x2={4 + i * 4} y2="42" />
              <line x1="4" y1={2 + i * 4} x2="44" y2={2 + i * 4} />
            </g>
          ))}
        </svg>
        <LogoSymbol size={360} label="BunonBrain symbol" className="relative h-full w-full" />
        <span className="absolute left-[52%] top-[6%] flex size-7 items-center justify-center rounded-full bg-[#16324F] font-plexmono text-[0.72rem] text-white">1</span>
        <span className="absolute left-[6%] top-[52%] flex size-7 items-center justify-center rounded-full bg-[#16324F] font-plexmono text-[0.72rem] text-white">2</span>
        <span className="absolute right-[4%] top-[72%] flex size-7 items-center justify-center rounded-full bg-[#059669] font-plexmono text-[0.72rem] text-white">3</span>
      </div>
      <ol className="space-y-7">
        {notes.map((n) => (
          <li key={n.n} className="flex gap-4">
            <span className={`mt-0.5 flex size-7 shrink-0 items-center justify-center rounded-full font-plexmono text-[0.72rem] text-white ${n.n === "3" ? "bg-[#059669]" : "bg-[#16324F]"}`}>
              {n.n}
            </span>
            <div>
              <h3 className="font-[family-name:var(--font-wordmark)] text-[1.25rem] font-semibold">{n.title}</h3>
              <p className="mt-1 max-w-[52ch] leading-relaxed text-[#334155]">{n.body}</p>
            </div>
          </li>
        ))}
        <li className="flex gap-4">
          <span className="mt-0.5 flex size-7 shrink-0 items-center justify-center rounded-full border border-[#16324F] font-plexmono text-[0.72rem]">+</span>
          <div>
            <h3 className="font-[family-name:var(--font-wordmark)] text-[1.25rem] font-semibold">
              <span className="text-[#16324F]">Bunon</span>
              <span className="text-[#059669]">Brain</span>
            </h3>
            <p className="mt-1 max-w-[52ch] leading-relaxed text-[#334155]">
              <span lang="bn">বুনন</span> (bunon) means weaving. Navy carries the craft, emerald carries the intelligence
              that runs alongside it.
            </p>
          </div>
        </li>
      </ol>
    </div>
  );
}

export default function BrandPage() {
  return (
    <div className={`${plexSans.variable} ${plexMono.variable} ${wordmarkFont.variable} min-h-screen bg-white font-plex text-[#16324F]`}>
      <header className="sticky top-0 z-10 border-b border-[#E2E8F0] bg-white/95 backdrop-blur">
        <div className="mx-auto flex h-16 max-w-[1200px] items-center gap-4 px-4 md:px-8">
          <Link href="/" className="inline-flex min-h-11 items-center" aria-label="BunonBrain home">
            <Logo height={32} />
          </Link>
          <span className="hidden font-plexmono text-[0.72rem] uppercase tracking-[0.16em] text-[#64748B] sm:inline">Brand identity</span>
          <nav aria-label="Brand sections" className="ml-auto hidden gap-1 text-[0.9rem] md:flex">
            {[
              ["#concept", "Concept"],
              ["#variations", "Variations"],
              ["#colour", "Colour"],
              ["#type", "Typography"],
              ["#downloads", "Downloads"],
            ].map(([href, label]) => (
              <a key={href} href={href} className="inline-flex min-h-11 items-center rounded-md px-3 text-[#334155] hover:text-[#16324F]">
                {label}
              </a>
            ))}
          </nav>
        </div>
      </header>

      <main id="main" className="mx-auto max-w-[1200px] px-4 md:px-8">
        {/* Main logo, prominently on white. */}
        <section aria-labelledby="brand-title" className="py-16 md:py-24">
          <p className="font-plexmono text-[0.72rem] uppercase tracking-[0.16em] text-[#64748B]">Bangladesh RMG Workforce OS</p>
          <h1 id="brand-title" className="sr-only">
            BunonBrain brand identity
          </h1>
          <div className="mt-10 flex justify-center rounded-2xl border border-[#E2E8F0] px-6 py-16 md:py-24">
            <Logo height={120} label="BunonBrain" className="h-auto w-full max-w-[680px]" />
          </div>
          <p className="mt-8 text-center font-[family-name:var(--font-wordmark)] text-[1.35rem] font-semibold tracking-[-0.005em] md:text-[1.6rem]">
            <span className="text-[#16324F]">Smart Workforce.</span> <span className="text-[#059669]">Stronger RMG.</span>
          </p>
        </section>

        <Section
          id="concept"
          eyebrow="01 · Symbol"
          title="One letter, three ideas"
          intro="The symbol is built on a 48-unit grid from a single stroke weight and round joins, so it stays crisp from a 16 px favicon to a factory gate sign."
        >
          <SymbolAnatomy />
        </Section>

        <Section
          id="variations"
          eyebrow="02 · Variations"
          title="Logo family"
          intro="Use the primary logo wherever possible. Switch to the dark version on navy or photography, and to a one-colour version only when colour is not available."
        >
          <div className="grid gap-5 md:grid-cols-2">
            <LogoTile label="Primary · white background" bg="#FFFFFF" tone="color" border />
            <LogoTile label="Dark background · deep navy" bg={LOGO_COLORS.navyDeep} tone="onDark" />
            <LogoTile label="Light background · light green" bg={LOGO_COLORS.lightGreen} tone="color" />
            <LogoTile label="Dark background · primary navy" bg={LOGO_COLORS.navy} tone="onDark" />
            <LogoTile label="Monochrome · navy" bg="#F8FAFC" tone="mono" mono={LOGO_COLORS.navy} border />
            <LogoTile label="Monochrome · black" bg="#FFFFFF" tone="mono" mono="#000000" border />
            <LogoTile label="Monochrome · white on emerald" bg={LOGO_COLORS.emerald} tone="mono" mono="#FFFFFF" />
            <Tile label="Wordmark only · where the symbol already appears nearby" bg="#FFFFFF" border>
              <Wordmark height={44} label="BunonBrain wordmark" className="h-auto max-w-full" />
            </Tile>
          </div>

          <h3 className="mt-14 font-[family-name:var(--font-wordmark)] text-[1.4rem] font-semibold">Standalone symbol and app icon</h3>
          <p className="mt-2 max-w-[60ch] text-[#334155]">
            For favicons, the mobile app, social avatars and anywhere the full logo will not fit. Below 32 px the symbol
            switches to a cut with one bolder wave so it never fills in.
          </p>
          <div className="mt-8 grid gap-5 sm:grid-cols-2 lg:grid-cols-4">
            <Tile label="Symbol · white" bg="#FFFFFF" border>
              <LogoSymbol size={120} label="Symbol on white" />
            </Tile>
            <Tile label="Symbol · on dark" bg={LOGO_COLORS.navyDeep}>
              <LogoSymbol size={120} tone="onDark" label="Symbol on dark" />
            </Tile>
            <Tile label="App icon" bg="#F8FAFC" border>
              <LogoAppIcon size={120} label="App icon" />
            </Tile>
            <Tile label="Sizes · 64 / 48 / 32 / 16 px" bg="#FFFFFF" border>
              <div className="flex items-end gap-4">
                {[64, 48, 32, 16].map((s) => (
                  <LogoAppIcon key={s} size={s} radius={s <= 16 ? 9 : 11} label={`App icon ${s} pixels`} />
                ))}
              </div>
            </Tile>
          </div>

          <div className="mt-14 grid gap-8 md:grid-cols-2">
            <div>
              <h3 className="font-[family-name:var(--font-wordmark)] text-[1.4rem] font-semibold">Clear space and minimum size</h3>
              <p className="mt-2 text-[#334155]">
                Keep clear space on every side equal to the height of the hanger hook (the dashed frame). Never set the
                horizontal logo below 24 px high on screen or 8 mm in print; below that, use the symbol.
              </p>
              <div className="mt-6 flex justify-center rounded-xl border border-[#E2E8F0] p-6">
                <div className="relative p-[22px] outline-dashed outline-1 outline-[#059669]/60">
                  <Logo height={56} label="Logo with clear space" className="h-auto max-w-full" />
                </div>
              </div>
            </div>
            <div>
              <h3 className="font-[family-name:var(--font-wordmark)] text-[1.4rem] font-semibold">Do not</h3>
              <ul className="mt-3 space-y-2.5 text-[#334155]">
                {[
                  "Recolour the symbol or swap the navy and emerald halves of the wordmark.",
                  "Stretch, condense, rotate or outline the logo, or rebuild the wordmark in another typeface.",
                  "Add shadows, glows, bevels, 3D effects or gradients.",
                  "Place the colour logo on mid-tone or busy backgrounds; use the dark or one-colour version.",
                  "Write the name any other way than BunonBrain: not Bunon Brain, Bunonbrain or BUNONBRAIN in running text.",
                ].map((t) => (
                  <li key={t} className="flex gap-3">
                    <span aria-hidden="true" className="mt-[9px] block h-[2px] w-3 shrink-0 bg-[#DC2626]" />
                    <span>{t}</span>
                  </li>
                ))}
              </ul>
            </div>
          </div>
        </Section>

        <Section
          id="colour"
          eyebrow="03 · Colour"
          title="Colour palette"
          intro="Navy leads, emerald accents. Keep emerald to roughly a fifth of any composition, and use flat colour: no gradients."
        >
          <ul className="grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
            {PALETTE.map((c) => (
              <li key={c.hex} className="overflow-hidden rounded-xl border border-[#E2E8F0]">
                <div className="flex h-32 items-end p-4" style={{ background: c.hex }}>
                  <span className={`font-plexmono text-[0.8rem] ${c.dark ? "text-white" : "text-[#16324F]"}`}>{c.hex}</span>
                </div>
                <div className="p-4">
                  <p className="font-semibold">{c.name}</p>
                  <p className="mt-1 font-plexmono text-[0.74rem] text-[#64748B]">
                    HEX {c.hex} · RGB {c.rgb}
                  </p>
                  <p className="mt-2 text-[0.88rem] leading-snug text-[#334155]">{c.role}</p>
                </div>
              </li>
            ))}
          </ul>
          <p className="mt-6 max-w-[70ch] text-[0.92rem] text-[#334155]">
            On screens, emerald on deep navy is lifted to <span className="font-plexmono">{LOGO_COLORS.emeraldOnDark}</span> so the
            waves and “Brain” keep at least 4.5:1 contrast. Print always uses the five colours above.
          </p>
        </Section>

        <Section
          id="type"
          eyebrow="04 · Typography"
          title="Typefaces"
          intro="A geometric sans for the name, a humanist sans for reading, and a mono for figures and labels."
        >
          <div className="grid gap-5 lg:grid-cols-3">
            <div className="rounded-xl border border-[#E2E8F0] p-6">
              <p className="font-plexmono text-[0.72rem] uppercase tracking-[0.14em] text-[#64748B]">Wordmark · Outfit Bold</p>
              <p className="mt-4 font-[family-name:var(--font-wordmark)] text-[3.4rem] font-bold leading-none tracking-[-0.012em]">
                <span className="text-[#16324F]">Bunon</span>
                <span className="text-[#059669]">Brain</span>
              </p>
              <p className="mt-5 font-[family-name:var(--font-wordmark)] text-[1.05rem] leading-relaxed text-[#334155]">
                ABCDEFGHIJKLMNOPQRSTUVWXYZ
                <br />
                abcdefghijklmnopqrstuvwxyz 0123456789
              </p>
              <p className="mt-4 text-[0.86rem] text-[#64748B]">
                Geometric, open and even. The logo uses outlined Outfit at weight 680 with −1.2% tracking; never retype it.
              </p>
            </div>
            <div className="rounded-xl border border-[#E2E8F0] p-6">
              <p className="font-plexmono text-[0.72rem] uppercase tracking-[0.14em] text-[#64748B]">Interface &amp; body · IBM Plex Sans</p>
              <p className="mt-4 text-[2rem] font-semibold leading-tight">Line 7 is 38 minutes behind plan.</p>
              <p className="mt-4 leading-relaxed text-[#334155]">
                Clear at small sizes and comfortable in long reports. Regular 400 for body, Medium 500 for labels, SemiBold 600
                for headings.
              </p>
            </div>
            <div className="rounded-xl border border-[#E2E8F0] p-6">
              <p className="font-plexmono text-[0.72rem] uppercase tracking-[0.14em] text-[#64748B]">Data &amp; labels · IBM Plex Mono</p>
              <p className="mt-4 font-plexmono text-[1.5rem] leading-snug">
                PO 4471 · 341/400
                <br />
                DHU 3.1%
              </p>
              <p className="mt-4 leading-relaxed text-[#334155]">
                For figures, codes, timestamps and small uppercase labels with wide tracking.
              </p>
            </div>
          </div>
          <div className="mt-8 rounded-xl bg-[#16324F] p-8 text-white md:p-10">
            <p className="font-plexmono text-[0.72rem] uppercase tracking-[0.16em] text-[#D1FAE5]">Tagline</p>
            <p className="mt-3 font-[family-name:var(--font-wordmark)] text-[2rem] font-semibold leading-tight md:text-[2.6rem]">
              Smart Workforce. <span className="text-[#34D399]">Stronger RMG.</span>
            </p>
            <p className="mt-3 text-[0.92rem] text-white/75">{TAGLINE} Set in Outfit SemiBold, sentence case, with both full stops.</p>
          </div>
        </Section>

        <Section id="downloads" eyebrow="05 · Files" title="Downloads" intro="Vector files for print and the web, and PNG icons for app stores.">
          <ul className="grid gap-3 sm:grid-cols-2">
            {DOWNLOADS.map((d) => (
              <li key={d.file}>
                <a
                  href={`/brand/${d.file}`}
                  download
                  className="flex min-h-14 items-center justify-between gap-4 rounded-lg border border-[#E2E8F0] px-4 py-3 hover:border-[#059669] focus-visible:outline focus-visible:outline-2 focus-visible:outline-[#059669]"
                >
                  <span>
                    <span className="block font-medium">{d.label}</span>
                    <span className="block text-[0.82rem] text-[#64748B]">{d.note}</span>
                  </span>
                  <span className="font-plexmono text-[0.74rem] text-[#047854]">{d.file.split(".").pop()?.toUpperCase()} ↓</span>
                </a>
              </li>
            ))}
          </ul>
        </Section>
      </main>

      <footer className="border-t border-[#E2E8F0] bg-[#10263D]">
        <div className="mx-auto flex max-w-[1200px] flex-col gap-4 px-4 py-10 md:flex-row md:items-center md:justify-between md:px-8">
          <Logo height={30} tone="onDark" label="BunonBrain" />
          <p className="font-plexmono text-[0.74rem] text-[#A7B1BE]">© {new Date().getFullYear()} BunonBrain · {TAGLINE}</p>
        </div>
      </footer>
    </div>
  );
}
