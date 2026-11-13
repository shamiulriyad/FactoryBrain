import { FormPage } from "@/components/site/FormPage";
import { InquiryForm } from "@/components/site/InquiryForm";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Report a problem",
  description: "Report a bug, error or unexpected behaviour in BunonBrain or on this website.",
  path: "/support/report-a-problem",
});

export default function ReportProblemPage() {
  return (
    <FormPage
      crumbs={[{ label: "Support", href: "/support" }, { label: "Report a problem" }]}
      eyebrow="Report a problem"
      title="Something not working as it should?"
      lead="Tell us what happened. Clear steps to reproduce the problem help us fix it quickly."
      asideTitle="A good report includes"
      points={["What you were trying to do", "What you expected, and what happened instead", "The page or module, and roughly when it happened", "Any error message, copied exactly"]}
      asideFooter={<p>For a security vulnerability, choose &ldquo;Security&rdquo; below and please don&apos;t share the details publicly.</p>}
      formTitle="Problem report"
    >
      <InquiryForm
        kind="problem"
        fields={["company", "subject"]}
        extra={[
          { key: "where", label: "Where", type: "select", required: true, options: ["BunonBrain application", "This website", "Security"].map((v) => ({ value: v, label: v })) },
          { key: "severity", label: "Impact", type: "select", options: ["Minor or cosmetic", "A feature doesn't work", "Blocks my work"].map((v) => ({ value: v, label: v })) },
          { key: "page", label: "Page or address", type: "text", placeholder: "e.g. /app/production" },
        ]}
        messageLabel="What happened?"
        messagePlaceholder="Steps to reproduce, what you expected, what happened, and any error text."
        submitLabel="Send report"
      />
    </FormPage>
  );
}
