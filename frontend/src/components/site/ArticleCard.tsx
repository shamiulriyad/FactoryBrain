import Link from "next/link";
import { ArrowRight } from "lucide-react";
import { formatDate, type Article } from "@/content/site/articles";
import { cn } from "@/lib/cn";

export function ArticleCard({ a, large = false }: { a: Article; large?: boolean }) {
  return (
    <Link
      href={`/blog/${a.slug}`}
      className={cn(
        "group flex h-full flex-col rounded-2xl border border-brand-border bg-white p-7 transition-[border-color,box-shadow,transform] hover:-translate-y-0.5 hover:border-brand-navy/25 hover:shadow-[0_16px_40px_-20px_rgba(16,38,61,0.25)]",
        large && "site-hero md:p-10",
      )}
    >
      <span className="font-plexmono text-[0.7rem] uppercase tracking-[0.14em] text-brand-emerald-ink">{a.category}</span>
      <span className={cn("mt-3 font-heading font-semibold leading-snug tracking-[-0.01em] text-brand-text", large ? "text-[1.7rem] md:text-[2rem]" : "text-[1.2rem]")}>{a.title}</span>
      <span className="mt-3 flex-1 leading-relaxed text-brand-body">{a.excerpt}</span>
      <span className="mt-6 flex items-center justify-between text-[0.85rem] text-brand-muted">
        <span>
          {formatDate(a.date)} · {a.minutes} min read
        </span>
        <ArrowRight size={16} aria-hidden className="text-brand-emerald-ink transition-transform group-hover:translate-x-0.5" />
      </span>
    </Link>
  );
}
