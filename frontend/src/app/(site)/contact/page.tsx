import Link from "next/link";
import { FormPage } from "@/components/site/FormPage";
import { InquiryForm } from "@/components/site/InquiryForm";
import { SITE } from "@/lib/site/config";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Contact us",
  description: "Contact BunonBrain about the product, a demo, a consultation, partnerships or media enquiries.",
  path: "/contact",
});

const a = "font-medium text-brand-emerald-ink underline-offset-4 hover:underline";

export default function ContactPage() {
  const { email, phone, address } = SITE.contact;
  return (
    <FormPage
      crumbs={[{ label: "Contact" }]}
      eyebrow="Contact us"
      title="Let's talk about your plant."
      lead="Questions about BunonBrain, a demo, a consultation or a partnership: send us a message and the right person will reply."
      asideTitle="Choose the fastest route"
      points={[
        <>
          <Link href="/demo" className={a}>Request a demo</Link> to see the product on a simulated plant.
        </>,
        <>
          <Link href="/consultation" className={a}>Book a consultation</Link> to discuss your lines and priorities.
        </>,
        <>
          <Link href="/support" className={a}>Contact support</Link> if you already use BunonBrain.
        </>,
        <>
          <Link href="/partners" className={a}>Partnerships</Link> and <Link href="/press" className={a}>press</Link> have their own forms.
        </>,
      ]}
      asideFooter={
        email || phone || address ? (
          <dl className="space-y-2">
            {email && (
              <div>
                <dt className="inline font-medium text-brand-text">Email: </dt>
                <dd className="inline">
                  <a href={`mailto:${email}`} className="text-brand-emerald-ink">
                    {email}
                  </a>
                </dd>
              </div>
            )}
            {phone && (
              <div>
                <dt className="inline font-medium text-brand-text">Phone: </dt>
                <dd className="inline">{phone}</dd>
              </div>
            )}
            {address && (
              <div>
                <dt className="inline font-medium text-brand-text">Office: </dt>
                <dd className="inline">{address}</dd>
              </div>
            )}
          </dl>
        ) : (
          <p>Messages sent through this form are stored securely and read by the BunonBrain team.</p>
        )
      }
      formTitle="Send us a message"
    >
      <InquiryForm
        kind="contact"
        fields={["phone", "company", "jobTitle", "subject"]}
        extra={[{ key: "topic", label: "Topic", type: "select", options: ["Product question", "Pricing", "Partnership", "Media", "Other"].map((v) => ({ value: v, label: v })) }]}
        messageLabel="Your message"
      />
    </FormPage>
  );
}
