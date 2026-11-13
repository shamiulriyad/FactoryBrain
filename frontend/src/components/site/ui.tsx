import Link from "next/link";
import type { ReactNode } from "react";
import {
  ArrowRight,
  BarChart3,
  Bell,
  Bot,
  BrainCircuit,
  CheckCircle2,
  ChevronRight,
  Clock,
  Factory,
  FileText,
  Gauge,
  Layers,
  MessageSquareText,
  Plug,
  ScanSearch,
  ShieldCheck,
  Users,
  type LucideIcon,
} from "lucide-react";
import type { IconName, ModuleStatus } from "@/content/site/modules";
import { STATUS_LABEL } from "@/content/site/modules";
import { cn } from "@/lib/cn";

/* Design tokens for the public site live in tailwind.config.ts (brand-*)
   and globals.css (.site-*). Components here only compose them. */

export function Container({ children, className, wide = false }: { children: ReactNode; className?: string; wide?: boolean }) {
  return <div className={cn("mx-auto w-full px-4 sm:px-6 lg:px-8", wide ? "max-w-[1320px]" : "max-w-[1200px]", className)}>{children}</div>;
}

const TONES = {
  white: "bg-white",
  off: "bg-brand-offwhite",
  navy: "site-on-dark bg-brand-navy-deep text-white",
  mint: "bg-brand-emerald-light/40",
} as const;

export function Section({
  children,
  tone = "white",
  id,
  className,
  label,
}: {
  children: ReactNode;
  tone?: keyof typeof TONES;
  id?: string;
  className?: string;
  /** aria-labelledby target id. */
  label?: string;
}) {
  return (
    <section id={id} aria-labelledby={label} className={cn("relative py-20 md:py-28", TONES[tone], className)}>
      {children}
    </section>
  );
}

export function Eyebrow({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <p className={cn("font-plexmono text-[0.72rem] font-medium uppercase tracking-[0.16em] text-brand-emerald-ink", className)}>
      {children}
    </p>
  );
}

export function SectionHeader({
  eyebrow,
  title,
  lead,
  id,
  align = "left",
  className,
  as: As = "h2",
}: {
  eyebrow?: string;
  title: ReactNode;
  lead?: ReactNode;
  id?: string;
  align?: "left" | "center";
  className?: string;
  as?: "h1" | "h2" | "h3";
}) {
  return (
    <div className={cn("max-w-[46rem]", align === "center" && "mx-auto text-center", className)}>
      {eyebrow && <Eyebrow>{eyebrow}</Eyebrow>}
      <As id={id} className="mt-3 font-heading text-[2rem] font-semibold leading-[1.1] tracking-[-0.02em] text-brand-text sm:text-[2.5rem] md:text-[2.85rem]">
        {title}
      </As>
      {lead && <p className="mt-5 text-[1.05rem] leading-[1.7] text-brand-body md:text-[1.15rem]">{lead}</p>}
    </div>
  );
}

const BUTTONS = {
  primary: "bg-brand-emerald-ink text-white shadow-[0_1px_2px_rgba(16,38,61,0.16)] hover:bg-[rgb(var(--brand-emerald-ink-hover))]",
  secondary: "border border-brand-border bg-white text-brand-text hover:border-brand-navy/40 hover:bg-brand-offwhite",
  dark: "bg-brand-navy text-white hover:bg-brand-navy-deep",
  onDark: "border border-white/25 text-white hover:border-white/60 hover:bg-white/5",
  link: "px-0 text-brand-emerald-ink underline-offset-4 hover:underline",
} as const;

export function ButtonLink({
  href,
  children,
  variant = "primary",
  arrow = false,
  className,
  size = "md",
}: {
  href: string;
  children: ReactNode;
  variant?: keyof typeof BUTTONS;
  arrow?: boolean;
  className?: string;
  size?: "md" | "lg";
}) {
  return (
    <Link
      href={href}
      className={cn(
        "group inline-flex items-center justify-center gap-2 rounded-lg font-medium transition-colors focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand-emerald",
        variant !== "link" && (size === "lg" ? "min-h-12 px-6 text-[1rem]" : "min-h-11 px-5 text-[0.95rem]"),
        variant === "link" && "min-h-11",
        BUTTONS[variant],
        className,
      )}
    >
      {children}
      {arrow && <ArrowRight size={16} aria-hidden className="transition-transform group-hover:translate-x-0.5" />}
    </Link>
  );
}

const STATUS_STYLE: Record<ModuleStatus, string> = {
  available: "border-brand-emerald/30 bg-brand-emerald-light/60 text-[rgb(var(--brand-success-ink))]",
  demo: "border-brand-info/25 bg-brand-info/10 text-brand-info",
  planned: "border-brand-warning/40 bg-brand-warning/10 text-[rgb(var(--brand-warning-ink))]",
};

export function StatusBadge({ status, className }: { status: ModuleStatus; className?: string }) {
  return (
    <span className={cn("inline-flex items-center rounded-full border px-2.5 py-0.5 font-plexmono text-[0.68rem] font-medium uppercase tracking-[0.08em]", STATUS_STYLE[status], className)}>
      {STATUS_LABEL[status]}
    </span>
  );
}

const ICONS: Record<IconName, LucideIcon> = {
  factory: Factory,
  gauge: Gauge,
  scan: ScanSearch,
  file: FileText,
  check: CheckCircle2,
  message: MessageSquareText,
  bot: Bot,
  chart: BarChart3,
  shield: ShieldCheck,
  users: Users,
  clock: Clock,
  plug: Plug,
  layers: Layers,
  brain: BrainCircuit,
  bell: Bell,
};

export function IconTile({ name, className, tone = "light" }: { name: IconName; className?: string; tone?: "light" | "dark" }) {
  const Icon = ICONS[name];
  return (
    <span
      aria-hidden="true"
      className={cn(
        "inline-flex size-11 shrink-0 items-center justify-center rounded-xl",
        tone === "light" ? "bg-brand-emerald-light/70 text-brand-emerald-ink ring-1 ring-brand-emerald/15" : "bg-white/10 text-[rgb(var(--brand-emerald-on-dark))] ring-1 ring-white/10",
        className,
      )}
    >
      <Icon size={20} strokeWidth={1.8} />
    </span>
  );
}

export function Card({ children, className, href }: { children: ReactNode; className?: string; href?: string }) {
  const base = "rounded-2xl border border-brand-border bg-white p-6 md:p-7";
  if (href) {
    return (
      <Link
        href={href}
        className={cn(base, "group block transition-[border-color,box-shadow,transform] hover:-translate-y-0.5 hover:border-brand-navy/25 hover:shadow-[0_12px_32px_-12px_rgba(16,38,61,0.18)] focus-visible:outline focus-visible:outline-2 focus-visible:outline-brand-emerald", className)}
      >
        {children}
      </Link>
    );
  }
  return <div className={cn(base, className)}>{children}</div>;
}

export interface Crumb {
  label: string;
  href?: string;
}

export function Breadcrumbs({ items, className }: { items: Crumb[]; className?: string }) {
  return (
    <nav aria-label="Breadcrumb" className={className}>
      <ol className="flex flex-wrap items-center gap-1.5 text-[0.85rem] text-brand-muted">
        <li>
          <Link href="/" className="hover:text-brand-text">
            Home
          </Link>
        </li>
        {items.map((c, i) => (
          <li key={c.label} className="flex items-center gap-1.5">
            <ChevronRight size={14} aria-hidden />
            {c.href && i < items.length - 1 ? (
              <Link href={c.href} className="hover:text-brand-text">
                {c.label}
              </Link>
            ) : (
              <span aria-current="page" className="text-brand-body">
                {c.label}
              </span>
            )}
          </li>
        ))}
      </ol>
    </nav>
  );
}

/** Top of every inner page: breadcrumbs, eyebrow, H1, lead, actions, optional visual. */
export function PageHero({
  eyebrow,
  title,
  lead,
  crumbs,
  actions,
  aside,
  children,
}: {
  eyebrow?: string;
  title: ReactNode;
  lead?: ReactNode;
  crumbs?: Crumb[];
  actions?: ReactNode;
  aside?: ReactNode;
  children?: ReactNode;
}) {
  return (
    <header className="site-hero relative overflow-hidden border-b border-brand-border">
      <div aria-hidden className="site-grid pointer-events-none absolute inset-0" />
      <Container wide className={cn("relative pb-16 pt-10 md:pb-20 md:pt-14", aside && "grid items-center gap-12 lg:grid-cols-[minmax(0,1.05fr)_minmax(0,1fr)]")}>
        <div>
          {crumbs && <Breadcrumbs items={crumbs} className="mb-8" />}
          {eyebrow && <Eyebrow>{eyebrow}</Eyebrow>}
          <h1 className="mt-3 max-w-[48rem] font-heading text-[2.4rem] font-semibold leading-[1.06] tracking-[-0.025em] text-brand-text sm:text-[3rem] md:text-[3.5rem]">
            {title}
          </h1>
          {lead && <p className="mt-6 max-w-[42rem] text-[1.08rem] leading-[1.7] text-brand-body md:text-[1.2rem]">{lead}</p>}
          {actions && <div className="mt-9 flex flex-wrap items-center gap-3">{actions}</div>}
          {children}
        </div>
        {aside && <div className="relative">{aside}</div>}
      </Container>
    </header>
  );
}

export function CtaBand({
  title = "See BunonBrain on a running garment plant",
  body = "We walk you through the product on a simulated factory and answer questions about your own lines.",
  primary = { label: "Request a demo", href: "/demo" },
  secondary = { label: "Book a consultation", href: "/consultation" },
}: {
  title?: string;
  body?: string;
  primary?: { label: string; href: string };
  secondary?: { label: string; href: string } | null;
}) {
  return (
    <section aria-label="Next step" className="bg-white py-20 md:py-24">
      <Container>
        <div className="site-cta relative overflow-hidden rounded-3xl px-6 py-14 text-white sm:px-12 md:py-16">
          <div aria-hidden className="site-weave pointer-events-none absolute inset-0 opacity-60" />
          <div className="relative grid items-center gap-8 md:grid-cols-[minmax(0,1.4fr)_auto]">
            <div>
              <h2 className="font-heading text-[1.9rem] font-semibold leading-tight tracking-[-0.02em] md:text-[2.4rem]">{title}</h2>
              <p className="mt-4 max-w-[38rem] text-[1.05rem] leading-relaxed text-white/80">{body}</p>
            </div>
            <div className="flex flex-wrap gap-3 md:justify-end">
              <ButtonLink href={primary.href} size="lg" arrow>
                {primary.label}
              </ButtonLink>
              {secondary && (
                <ButtonLink href={secondary.href} size="lg" variant="onDark">
                  {secondary.label}
                </ButtonLink>
              )}
            </div>
          </div>
        </div>
      </Container>
    </section>
  );
}

/** Honest placeholder for sections that have no published content yet. */
export function EmptyState({
  icon = "layers",
  title,
  body,
  action,
}: {
  icon?: IconName;
  title: string;
  body: ReactNode;
  action?: ReactNode;
}) {
  return (
    <div className="rounded-2xl border border-dashed border-brand-navy/20 bg-brand-offwhite px-6 py-14 text-center">
      <IconTile name={icon} className="mx-auto" />
      <h3 className="mt-5 font-heading text-[1.35rem] font-semibold text-brand-text">{title}</h3>
      <div className="mx-auto mt-2 max-w-[36rem] leading-relaxed text-brand-body">{body}</div>
      {action && <div className="mt-6 flex justify-center">{action}</div>}
    </div>
  );
}

export function CheckList({ items, className, tone = "light" }: { items: ReactNode[]; className?: string; tone?: "light" | "dark" }) {
  return (
    <ul className={cn("space-y-3", className)}>
      {items.map((t, i) => (
        <li key={i} className="flex gap-3">
          <CheckCircle2 size={18} aria-hidden className={cn("mt-[3px] shrink-0", tone === "light" ? "text-brand-emerald" : "text-[rgb(var(--brand-emerald-on-dark))]")} />
          <span className={tone === "light" ? "text-brand-body" : "text-white/80"}>{t}</span>
        </li>
      ))}
    </ul>
  );
}
