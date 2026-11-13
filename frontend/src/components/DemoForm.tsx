import { InquiryForm } from "@/components/site/InquiryForm";
import { LINE_OPTIONS } from "@/lib/demoRequest";

/** "Book a demo" on the 3D tour page. Submits to POST /api/inquiries. */
export function DemoSection() {
  return (
    <section id="demo" aria-labelledby="demo-title" className="fb-offwhite relative z-20 border-t border-fb-line bg-fb-ink">
      <div className="mx-auto grid max-w-[1200px] gap-12 px-4 py-24 md:grid-cols-[1fr_1.1fr] md:gap-20 md:px-8 md:py-32">
        <div>
          <h2 id="demo-title" className="font-display text-[2.6rem] font-extrabold leading-none md:text-[3.6rem]">
            Book a demo
          </h2>
          <p className="mt-5 max-w-[30rem] leading-relaxed text-fb-text/90">
            Tell us a little about your factory. We&apos;ll walk you through BunonBrain using examples from a plant
            like yours, and talk through what it would take to start on one floor.
          </p>
        </div>
        <InquiryForm
          kind="demo"
          fields={["phone", "company"]}
          extra={[{ key: "lines", label: "Sewing lines", type: "select", required: true, options: LINE_OPTIONS }]}
          messageLabel="What would you like to see?"
          submitLabel="Request a demo"
        />
      </div>
    </section>
  );
}
