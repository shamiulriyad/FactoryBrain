"use client";

import Link from "next/link";
import { useEffect } from "react";
import { Container } from "@/components/site/ui";

/** Error boundary for public pages. The header and footer stay in place. */
export default function SiteError({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
  useEffect(() => {
    console.error(error);
  }, [error]);
  return (
    <section className="site-hero border-b border-brand-border">
      <Container className="py-24 md:py-32">
        <p className="font-plexmono text-[0.8rem] uppercase tracking-[0.16em] text-brand-error">Something went wrong</p>
        <h1 className="mt-3 max-w-[40rem] font-heading text-[2.4rem] font-semibold leading-[1.1] tracking-[-0.02em] text-brand-text md:text-[3rem]">
          This page couldn&apos;t be loaded.
        </h1>
        <p className="mt-5 max-w-[36rem] text-[1.08rem] leading-relaxed text-brand-body">
          Please try again. If the problem continues, let us know and include the reference below.
        </p>
        {error.digest && <p className="mt-4 font-plexmono text-[0.85rem] text-brand-muted">Reference: {error.digest}</p>}
        <div className="mt-9 flex flex-wrap gap-3">
          <button type="button" onClick={reset} className="inline-flex min-h-11 items-center rounded-lg bg-brand-emerald-ink px-5 font-medium text-white">
            Try again
          </button>
          <Link href="/support/report-a-problem" className="inline-flex min-h-11 items-center rounded-lg border border-brand-border bg-white px-5 font-medium text-brand-text">
            Report a problem
          </Link>
          <Link href="/" className="inline-flex min-h-11 items-center px-3 font-medium text-brand-navy">
            Go home
          </Link>
        </div>
      </Container>
    </section>
  );
}
