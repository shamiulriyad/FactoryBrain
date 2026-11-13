import Link from "next/link";
import { FormPage } from "@/components/site/FormPage";
import { InquiryForm } from "@/components/site/InquiryForm";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Contact support",
  description: "Get help with BunonBrain: documentation, the help center, system status and a direct support request form.",
  path: "/support",
});

const a = "font-medium text-brand-emerald-ink underline-offset-4 hover:underline";

export default function SupportPage() {
  return (
    <FormPage
      crumbs={[{ label: "Support" }]}
      eyebrow="Contact support"
      title="How can we help?"
      lead="Already using BunonBrain? Check the help center first; if you can't find the answer, send a support request with as much detail as you can."
      asideTitle="Before you write"
      points={[
        <>
          <Link href="/help" className={a}>Help center</Link>: guides for common tasks.
        </>,
        <>
          <Link href="/docs" className={a}>Documentation</Link>: how each module works.
        </>,
        <>
          <Link href="/status" className={a}>System status</Link>: is the service reachable?
        </>,
        <>
          <Link href="/support/report-a-problem" className={a}>Report a problem</Link> for bugs and errors.
        </>,
        <>
          <Link href="/support/feature-request" className={a}>Request a feature</Link> for ideas.
        </>,
      ]}
      asideFooter={<p>Never send passwords, API keys or workers&apos; personal information in a support request.</p>}
      formTitle="Support request"
    >
      <InquiryForm
        kind="support"
        fields={["company", "subject"]}
        extra={[
          {
            key: "module",
            label: "Module",
            type: "select",
            options: ["Sign-in & accounts", "Production planning", "Line board & alerts", "Quality inspection", "Merchandising", "Approvals", "Ask & knowledge", "Workforce", "Settings", "Other"].map((v) => ({ value: v, label: v })),
          },
          { key: "urgency", label: "Urgency", type: "select", options: ["Question", "Something isn't working", "Production is blocked"].map((v) => ({ value: v, label: v })) },
        ]}
        messageLabel="Describe what you need"
        submitLabel="Send support request"
      />
    </FormPage>
  );
}
