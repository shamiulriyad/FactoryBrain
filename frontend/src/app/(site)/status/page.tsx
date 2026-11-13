import Link from "next/link";
import { Container, PageHero, Section } from "@/components/site/ui";
import { pageMeta } from "@/lib/site/seo";
import { StatusBoard } from "./StatusBoard";

export const metadata = pageMeta({
  title: "System status",
  description: "Live status of the BunonBrain website and application API.",
  path: "/status",
});

export default function StatusPage() {
  return (
    <>
      <PageHero
        crumbs={[{ label: "Support", href: "/support" }, { label: "System status" }]}
        eyebrow="System status"
        title="Service status"
        lead="A live check of this website and the BunonBrain application API, including its database connection. The check runs when you open this page and every minute after."
      />
      <Section>
        <Container className="max-w-[880px]">
          <StatusBoard />
          <div className="mt-12 rounded-2xl border border-brand-border bg-brand-offwhite p-6 text-[0.95rem] leading-relaxed text-brand-body">
            <p className="font-medium text-brand-text">About this page</p>
            <p className="mt-1">
              This page reports the current state of the deployment serving this website. It does not keep an incident history
              yet. If something isn&apos;t working,{" "}
              <Link href="/support/report-a-problem" className="text-brand-emerald-ink underline underline-offset-2">
                report a problem
              </Link>
              .
            </p>
          </div>
        </Container>
      </Section>
    </>
  );
}
