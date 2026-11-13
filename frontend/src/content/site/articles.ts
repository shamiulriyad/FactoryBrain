import type { Block } from "./blocks";

/**
 * Blog articles. These are original explainers written by the BunonBrain
 * team about standard RMG measures and practices; they contain no customer
 * stories and no invented statistics. Formulas are the industry-standard
 * definitions.
 */
export type ArticleCategory = "RMG insights" | "Guides" | "Product";

export interface Article {
  slug: string;
  title: string;
  excerpt: string;
  category: ArticleCategory;
  date: string; // ISO
  minutes: number;
  featured?: boolean;
  body: Block[];
}

export const ARTICLES: Article[] = [
  {
    slug: "line-efficiency-from-smv",
    title: "Line efficiency from SMV: how to calculate it, and how not to",
    excerpt: "Efficiency is the most quoted number on a sewing floor and one of the most often miscalculated. Here is the standard formula, and the mistakes that distort it.",
    category: "RMG insights",
    date: "2026-11-13",
    minutes: 6,
    featured: true,
    body: [
      { t: "p", text: "Ask ten supervisors how efficient their line was yesterday and you may get ten different methods. Some divide output by target, some include helpers and some don't, some use clock hours and others available minutes. The result is a number that can't be compared between lines, let alone between factories." },
      { t: "h2", text: "The standard formula" },
      { t: "p", text: "Line efficiency compares the work the line produced, measured in standard minutes, with the minutes the line had available." },
      { t: "formula", text: "Efficiency % = (Pieces produced × SMV) ÷ (Operators × Minutes worked) × 100", caption: "SMV: standard minute value of the garment, from a time study or a recognised database." },
      { t: "p", text: "A line of 40 operators working a 480-minute shift has 19,200 available minutes. If it sews 900 shirts with an SMV of 14, it produced 12,600 standard minutes, an efficiency of 65.6%." },
      { t: "h2", text: "Five common mistakes" },
      { t: "ol", items: [
        "**Using target instead of SMV.** Output ÷ target measures plan attainment, not efficiency. If the target was set loosely, the line looks efficient when it isn't.",
        "**Leaving people out of the denominator.** Helpers, ironers and feeders on the line consume minutes. Decide once who counts and apply it everywhere.",
        "**Ignoring overtime.** Minutes worked must include overtime, or the evening hours inflate the result.",
        "**Mixing styles without weighting.** When a line changes style mid-day, multiply each style's output by its own SMV before adding.",
        "**Subtracting downtime silently.** Machine breakdowns and waiting for bundles are real losses. Report them as non-productive time rather than removing them from the minutes.",
      ] },
      { t: "h2", text: "Why consistency matters more than precision" },
      { t: "p", text: "An efficiency figure is only useful if the same method is used every day, on every line. A consistent number shows trends and differences between lines; an inconsistent one starts arguments." },
      { t: "note", text: "In BunonBrain, efficiency is calculated from recorded output, the style's SMV and the minutes available in the line's shifts. Nothing is typed in or estimated." },
    ],
  },
  {
    slug: "dhu-vs-defect-rate",
    title: "DHU and defect rate: two quality numbers, two different questions",
    excerpt: "Defects per hundred units and percentage defective are often used interchangeably. They answer different questions, and confusing them hides problems.",
    category: "RMG insights",
    date: "2026-11-13",
    minutes: 5,
    body: [
      { t: "p", text: "Quality reports in garment factories usually carry one of two numbers: DHU or defect percentage. They are related, but they are not the same, and choosing the wrong one can make a deteriorating line look stable." },
      { t: "h2", text: "Defects per hundred units (DHU)" },
      { t: "formula", text: "DHU = Total defects found ÷ Pieces inspected × 100" },
      { t: "p", text: "DHU counts every defect. A garment with three defects adds three. It tells you how much rework the line is generating." },
      { t: "h2", text: "Defect rate (percentage defective)" },
      { t: "formula", text: "Defect % = Defective pieces ÷ Pieces inspected × 100" },
      { t: "p", text: "Defect rate counts garments, not defects. A garment with three defects counts once. It tells you how many pieces need to go back." },
      { t: "h2", text: "When they disagree" },
      { t: "p", text: "If DHU rises while the defect rate stays flat, the same number of garments are failing, but each one has more problems. That usually points to a single operation or machine producing repeated faults, which is worth investigating at once." },
      { t: "h2", text: "Make the data trustworthy first" },
      { t: "ul", items: ["Record the line, operation and hour with each defect, so trends can be traced.", "Use one defect taxonomy across inspectors and shifts.", "Only count findings an inspector has confirmed. Automated detections are suggestions until reviewed."] },
      { t: "note", text: "BunonBrain calculates defect rates and DHU only from inspections an inspector has reviewed, using the factory's own defect categories." },
    ],
  },
  {
    slug: "line-balancing-basics",
    title: "Line balancing basics for sewing lines",
    excerpt: "Every sewing line runs at the speed of its slowest operation. A practical introduction to finding the bottleneck and spreading work more evenly.",
    category: "Guides",
    date: "2026-11-13",
    minutes: 7,
    featured: true,
    body: [
      { t: "p", text: "A sewing line is a chain of operations. However fast most operators work, output is capped by the operation that takes longest per piece. Line balancing is the practice of distributing work so that no single station holds the others back." },
      { t: "h2", text: "Key terms" },
      { t: "table", head: ["Term", "Meaning"], rows: [
        ["Takt time", "Available time ÷ required output: how often a finished piece must leave the line to meet the target."],
        ["Pitch time", "Total SMV of the garment ÷ number of operators: the ideal work content per operator."],
        ["Cycle time", "Observed time an operator takes for their operation, from a time study."],
        ["Bottleneck", "The station with the highest cycle time; it sets the line's real capacity."],
      ] },
      { t: "h2", text: "A simple balancing routine" },
      { t: "ol", items: [
        "Time-study each operation on the line, with several cycles per operator.",
        "Compare each station's cycle time with the pitch time.",
        "Identify stations well above pitch time (bottlenecks) and well below (spare capacity).",
        "Move part of a bottleneck's work to a skilled operator nearby, add a helper, or split the operation.",
        "Re-measure after the change. Balancing is a cycle, not a one-off event.",
      ] },
      { t: "h2", text: "Watch the bundles" },
      { t: "p", text: "Bundles piling up in front of a station are the most visible sign of a bottleneck; empty hands after it are the other. Work in progress between operations is a free, continuous balancing signal." },
      { t: "note", text: "BunonBrain records time-study observations, identifies the bottleneck and produces a balancing proposal. The proposal is applied only after it has been approved." },
    ],
  },
  {
    slug: "wip-between-operations",
    title: "What the bundle count tells a supervisor",
    excerpt: "Work in progress is often treated as a storage problem. On a sewing line it is the earliest warning that the line is out of balance.",
    category: "RMG insights",
    date: "2026-11-13",
    minutes: 4,
    body: [
      { t: "p", text: "Some WIP is necessary: each operator needs work waiting so they never sit idle. Too much WIP means money tied up in half-made garments, longer throughput times and quality problems discovered late." },
      { t: "h2", text: "Three patterns to look for" },
      { t: "ul", items: [
        "**Growing pile in front of one station.** That station is the bottleneck. Act within the hour.",
        "**Empty stations after a pile.** The line downstream is starved and losing minutes.",
        "**WIP rising everywhere.** Input is faster than the line can absorb; slow the feed or check the plan.",
      ] },
      { t: "h2", text: "Turning WIP into a routine" },
      { t: "p", text: "Agree a healthy range of bundles per station, check it at fixed times in each shift, and record what was done when a station falls outside it. Over time, those records show which operations need permanent attention." },
      { t: "note", text: "The BunonBrain line board shows WIP per line next to efficiency and non-productive time, and flags lines that need attention." },
    ],
  },
  {
    slug: "start-on-one-floor",
    title: "Starting digital on one floor: a practical rollout plan",
    excerpt: "Factory-wide launches are the hardest way to digitise a garment plant. A step-by-step plan for starting small and earning trust.",
    category: "Guides",
    date: "2026-11-13",
    minutes: 6,
    body: [
      { t: "p", text: "New systems fail on the floor when they ask people to change everything at once. Starting with one floor, the records it already keeps and a short list of measures gives supervisors time to trust what they see." },
      { t: "h2", text: "Step 1: choose the scope" },
      { t: "ul", items: ["One floor or a group of lines with a supervisor willing to try it", "Two or three measures that matter, such as hourly output, efficiency and WIP", "A clear owner on the management side"] },
      { t: "h2", text: "Step 2: set up the basics" },
      { t: "ul", items: ["Lines, shifts and styles with their SMV", "Accounts for the people involved, with the right roles", "Agreement on who records output, and when"] },
      { t: "h2", text: "Step 3: run in parallel" },
      { t: "p", text: "Keep existing reports for a few weeks and compare. Differences are useful: they show where definitions disagree, and resolving them builds confidence in both." },
      { t: "h2", text: "Step 4: widen deliberately" },
      { t: "p", text: "Once the first floor relies on the numbers, add the next source (inspection photos, order documents or machine readings) or the next floor. Each step should solve a problem people already feel." },
    ],
  },
  {
    slug: "why-approval-gates",
    title: "Why every recommendation in BunonBrain waits for a person",
    excerpt: "Software can spot a bottleneck faster than a person. It shouldn't move people between lines on its own. How approval gates keep automation accountable.",
    category: "Product",
    date: "2026-11-13",
    minutes: 4,
    body: [
      { t: "p", text: "BunonBrain uses agents and vision models to find problems: a line falling behind, a machine at risk, a defect in a photo. It would be technically simple to let them act. We chose not to." },
      { t: "h2", text: "Decisions affect people" },
      { t: "p", text: "Changing a plan or rebalancing a line moves work between people and changes their day. Those decisions belong to someone who knows the floor and can be asked about them later." },
      { t: "h2", text: "How the gate works" },
      { t: "ol", items: [
        "A recommendation becomes an approval request with a risk tier.",
        "Only people whose role allows it can decide; high-risk items need a higher permission.",
        "Rejecting requires a reason, so the next recommendation can improve.",
        "Every decision is written to an audit log that cannot be edited through the application.",
      ] },
      { t: "h2", text: "What it means in practice" },
      { t: "p", text: "Managers get a ranked queue instead of a stream of alerts, and the factory gets a record of who decided what, and why. That record is valuable in buyer audits and in the plant's own reviews." },
    ],
  },
];

export const articleBySlug = (slug: string) => ARTICLES.find((a) => a.slug === slug);

/** Company announcements. Add verified announcements here; empty shows an empty state. */
export const NEWS: { title: string; date: string; summary: string }[] = [];

/** Published customer case studies. Only real, approved stories belong here. */
export const CASE_STUDIES: { slug: string; title: string; summary: string }[] = [];

export const formatDate = (iso: string) => new Date(`${iso}T00:00:00Z`).toLocaleDateString("en-GB", { day: "numeric", month: "long", year: "numeric", timeZone: "UTC" });
