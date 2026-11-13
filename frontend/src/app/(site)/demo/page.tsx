import { FormPage } from "@/components/site/FormPage";
import { InquiryForm } from "@/components/site/InquiryForm";
import { LINE_OPTIONS } from "@/lib/demoRequest";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Request a demo",
  description: "Request a guided demo of BunonBrain on a simulated garment plant: line board, production planning, quality inspection, merchandising and approvals.",
  path: "/demo",
});

export default function DemoPage() {
  return (
    <FormPage
      crumbs={[{ label: "Request a demo" }]}
      eyebrow="Request a demo"
      title="See BunonBrain on a running garment plant."
      lead="A guided walkthrough of the product on a simulated factory, focused on the parts of the plant that matter most to you."
      asideTitle="What to expect"
      points={[
        "A live walkthrough of the modules relevant to your plant, on simulated data",
        "Time for questions about your lines, data and how a rollout could start",
        "An honest view of what is available today and what is planned",
        "No obligation and no commitment",
      ]}
      asideFooter={<p>We use the details you send only to arrange the demo.</p>}
      formTitle="Tell us about your factory"
    >
      <InquiryForm
        kind="demo"
        fields={["phone", "company", "jobTitle"]}
        extra={[
          { key: "lines", label: "Sewing lines", type: "select", required: true, options: LINE_OPTIONS },
          {
            key: "focus",
            label: "Main interest",
            type: "select",
            options: ["Production planning", "Line monitoring", "Quality inspection", "Merchandising", "Workforce", "Everything"].map((v) => ({ value: v, label: v })),
          },
        ]}
        messageLabel="What would you like to see?"
        messagePlaceholder="For example: how line efficiency is calculated, or how QC findings are reviewed."
        submitLabel="Request a demo"
      />
    </FormPage>
  );
}
