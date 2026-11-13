import { BookOpen, Bug, FileQuestion, LifeBuoy, Lightbulb, MessageCircle, Activity } from "lucide-react";
import { ResourceExplorer } from "@/components/site/ResourceExplorer";
import { Card, Container, PageHero, Section, SectionHeader } from "@/components/site/ui";
import { allResources } from "@/content/site/resources";
import { pageMeta } from "@/lib/site/seo";

export const metadata = pageMeta({
  title: "Help center",
  description: "Find answers about BunonBrain in the documentation and FAQs, check system status, or contact support.",
  path: "/help",
});

const ACTIONS = [
  { icon: BookOpen, title: "Documentation", body: "How each module works.", href: "/docs" },
  { icon: FileQuestion, title: "FAQs", body: "Short answers to common questions.", href: "/faq" },
  { icon: Activity, title: "System status", body: "Check whether the service is reachable.", href: "/status" },
  { icon: LifeBuoy, title: "Contact support", body: "Get help from our team.", href: "/support" },
  { icon: Bug, title: "Report a problem", body: "Bugs, errors and security issues.", href: "/support/report-a-problem" },
  { icon: Lightbulb, title: "Request a feature", body: "Ideas for the product.", href: "/support/feature-request" },
  { icon: MessageCircle, title: "Send feedback", body: "Tell us what you think.", href: "/feedback" },
];

export default function HelpPage() {
  const items = allResources().filter((r) => r.type === "Documentation" || r.type === "FAQ" || r.type === "Guide");
  return (
    <>
      <PageHero crumbs={[{ label: "Help center" }]} eyebrow="Help center" title="How can we help?" lead="Search the documentation and FAQs, or choose a topic below." />
      <Section className="py-14 md:py-16">
        <Container wide>
          <ResourceExplorer items={items} placeholder="Search help: e.g. password, approvals, inspection" />
        </Container>
      </Section>
      <Section tone="off" label="ha-title">
        <Container wide>
          <SectionHeader id="ha-title" eyebrow="Other ways to get help" title="Support options" />
          <div className="mt-10 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            {ACTIONS.map((a) => (
              <Card key={a.href} href={a.href} className="p-6">
                <span aria-hidden className="inline-flex size-10 items-center justify-center rounded-lg bg-brand-navy text-white">
                  <a.icon size={18} strokeWidth={1.8} />
                </span>
                <p className="mt-4 font-heading text-[1.08rem] font-semibold text-brand-text">{a.title}</p>
                <p className="mt-1 text-[0.93rem] text-brand-body">{a.body}</p>
              </Card>
            ))}
          </div>
        </Container>
      </Section>
    </>
  );
}
