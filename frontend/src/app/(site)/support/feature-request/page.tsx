import { FormPage } from "@/components/site/FormPage";
import { InquiryForm } from "@/components/site/InquiryForm";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Request a feature",
  description: "Suggest a feature or improvement for BunonBrain.",
  path: "/support/feature-request",
});

export default function FeatureRequestPage() {
  return (
    <FormPage
      crumbs={[{ label: "Support", href: "/support" }, { label: "Request a feature" }]}
      eyebrow="Request a feature"
      title="What should BunonBrain do next?"
      lead="Ideas from people who run lines, inspect garments and manage orders shape the product. Tell us the problem first; the solution can follow."
      asideTitle="Helpful details"
      points={["Who would use it, and in which role", "The problem it solves, with a real example", "How you handle it today", "How often it comes up"]}
      formTitle="Feature request"
    >
      <InquiryForm
        kind="feature"
        fields={["company", "jobTitle", "subject"]}
        extra={[
          {
            key: "area",
            label: "Area",
            type: "select",
            options: ["Production", "Quality", "Merchandising", "Workforce & attendance", "Line monitoring", "Reports", "AI & knowledge", "Access & security", "Other"].map((v) => ({ value: v, label: v })),
          },
        ]}
        messageLabel="Describe the problem and your idea"
        submitLabel="Send request"
      />
    </FormPage>
  );
}
