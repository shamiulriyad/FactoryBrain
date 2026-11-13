import { FormPage } from "@/components/site/FormPage";
import { InquiryForm } from "@/components/site/InquiryForm";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Feedback",
  description: "Share feedback about BunonBrain or this website.",
  path: "/feedback",
});

export default function FeedbackPage() {
  return (
    <FormPage
      crumbs={[{ label: "Feedback" }]}
      eyebrow="Feedback"
      title="Tell us what you think."
      lead="Praise, criticism and everything in between. Feedback about the product or this website helps us improve both."
      asideTitle="We read everything"
      points={["What worked well", "What was confusing or slow", "What you expected but didn't find"]}
      formTitle="Your feedback"
    >
      <InquiryForm
        kind="feedback"
        fields={["company"]}
        extra={[
          { key: "about", label: "About", type: "select", options: ["The product", "This website", "A demo or consultation", "Other"].map((v) => ({ value: v, label: v })) },
          { key: "rating", label: "Overall impression", type: "select", options: ["Very good", "Good", "Neutral", "Poor", "Very poor"].map((v) => ({ value: v, label: v })) },
        ]}
        messageLabel="Your feedback"
        submitLabel="Send feedback"
      />
    </FormPage>
  );
}
