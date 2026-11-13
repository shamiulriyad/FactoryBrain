import { FormPage } from "@/components/site/FormPage";
import { InquiryForm } from "@/components/site/InquiryForm";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Book a consultation",
  description: "Book a consultation with BunonBrain: a working session on your garment plant's operations, data and priorities.",
  path: "/consultation",
});

export default function ConsultationPage() {
  return (
    <FormPage
      crumbs={[{ label: "Book a consultation" }]}
      eyebrow="Book a consultation"
      title="A working session on your plant."
      lead="Talk through how your factory plans, monitors and reports today, where information gets stuck, and where BunonBrain could help first."
      asideTitle="We'll cover"
      points={["Your product mix, lines, sections and shifts", "Which records you keep today and in what form", "The decisions that most often come too late", "A sensible first step, on one floor or a few lines"]}
      formTitle="Request a consultation"
    >
      <InquiryForm
        kind="consultation"
        fields={["phone", "company", "jobTitle"]}
        extra={[
          { key: "factories", label: "Number of factories", type: "select", options: ["1", "2 to 3", "4 to 10", "More than 10"].map((v) => ({ value: v, label: v })) },
          { key: "time", label: "Preferred time", type: "select", options: ["Morning (Dhaka time)", "Afternoon (Dhaka time)", "Flexible"].map((v) => ({ value: v, label: v })) },
        ]}
        messageLabel="What would you like to discuss?"
        submitLabel="Request a consultation"
      />
    </FormPage>
  );
}
