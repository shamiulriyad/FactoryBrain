import type { IconName } from "./modules";

/**
 * Solutions are outcomes a factory wants; each one is delivered by product
 * modules (content/site/modules.ts). Copy describes what the modules do
 * today; nothing here promises results or figures.
 */
export interface Solution {
  slug: string;
  name: string;
  short: string;
  icon: IconName;
  headline: string;
  intro: string;
  challenges: { title: string; body: string }[];
  outcomes: { title: string; body: string }[];
  modules: string[];
  forRoles: string[];
}

export const SOLUTIONS: Solution[] = [
  {
    slug: "workforce-management",
    name: "Workforce Management",
    short: "People, roles and assignments tied to the production floor.",
    icon: "users",
    headline: "Know who is where, and who can see what.",
    intro:
      "A garment plant runs on thousands of people across lines, sections and shifts. BunonBrain keeps worker records and line assignments next to the production structure, under access rules that match each person's job.",
    challenges: [
      { title: "Separate registers", body: "Worker lists, line assignments and skills live in different files maintained by different people." },
      { title: "Uncontrolled access", body: "Shared spreadsheets expose personal information to everyone who has the link." },
      { title: "Assignments drift", body: "Moves between lines are made on the floor and rarely recorded." },
    ],
    outcomes: [
      { title: "One directory", body: "Worker profiles linked to factories, lines and workstations." },
      { title: "Access by role", body: "Employment details visible only to the roles that need them." },
      { title: "Recorded moves", body: "Workstation assignments made in the system, within each manager's scope." },
    ],
    modules: ["workforce", "access-control", "attendance-shifts"],
    forRoles: ["HR / Attendance Officer", "Factory Admin", "Production Supervisor"],
  },
  {
    slug: "factory-operations",
    name: "Factory Operations",
    short: "Live line status, alerts and the morning brief.",
    icon: "gauge",
    headline: "See the floor as it is, not as last week's report says it was.",
    intro:
      "BunonBrain brings line status, machine readings and floor alerts into one shared view, so supervisors and managers act on the same information at the same time.",
    challenges: [
      { title: "Late information", body: "Problems are discovered by walking the floor and reported at the end of the shift." },
      { title: "Many versions of the truth", body: "Each section keeps its own numbers, and meetings start by reconciling them." },
      { title: "Alert fatigue", body: "Without priorities, every warning looks equally urgent." },
    ],
    outcomes: [
      { title: "Shared line board", body: "Every line's efficiency, WIP and flags on one screen." },
      { title: "Alerts everyone sees", body: "Stored and broadcast in real time, acknowledged once for the whole team." },
      { title: "A short daily brief", body: "The top issues and pending decisions, ranked by what they put at risk." },
    ],
    modules: ["line-monitoring", "ai-agents", "integrations"],
    forRoles: ["Factory Manager", "Monitoring Operator", "Production Supervisor"],
  },
  {
    slug: "production-management",
    name: "Production Management",
    short: "From buyer order to plan, output and delivery risk.",
    icon: "factory",
    headline: "Plan with approval, record what happened, see what is at risk.",
    intro:
      "Connect buyer orders, merchandising documents, production plans and recorded output, so efficiency and delivery risk are calculated from the plant's own records.",
    challenges: [
      { title: "Plans in spreadsheets", body: "Plans drift from the floor and are compared with actuals too late to help." },
      { title: "Unclear order details", body: "Revised POs and tech packs circulate as attachments with no single confirmed version." },
      { title: "Estimated efficiency", body: "Efficiency is typed in rather than calculated from SMV and output." },
    ],
    outcomes: [
      { title: "Approved plans", body: "Plans become active only after an authorised manager approves them." },
      { title: "Confirmed order data", body: "Values extracted from documents, with their source, confirmed by a merchandiser." },
      { title: "Calculated measures", body: "Efficiency, WIP and delivery risk from recorded output." },
    ],
    modules: ["production-planning", "merchandising", "approvals-workflow"],
    forRoles: ["Planning & IE", "Merchandiser", "Factory Manager"],
  },
  {
    slug: "quality-management",
    name: "Quality Management",
    short: "Inspections with AI assistance and human review.",
    icon: "scan",
    headline: "Quality data you can trace and trust.",
    intro:
      "Record inspections with photos, let a vision model suggest defects where configured, and keep the inspector in charge of every finding. Quality measures come only from reviewed records.",
    challenges: [
      { title: "Paper records", body: "Defects are tallied by hand and summarised days later." },
      { title: "No traceability", body: "It is hard to link a defect to the line and hour that produced it." },
      { title: "Unverifiable automation", body: "Automated inspection is hard to trust when its output can't be checked." },
    ],
    outcomes: [
      { title: "Photo evidence", body: "Every inspection stored with its image and line." },
      { title: "Reviewed findings", body: "AI suggestions are confirmed or corrected by an inspector." },
      { title: "Reliable DHU", body: "Defect rates calculated from reviewed inspections only." },
    ],
    modules: ["quality-inspection", "reports-analytics"],
    forRoles: ["QC Inspector", "Quality Manager", "Factory Manager"],
  },
  {
    slug: "analytics-intelligence",
    name: "Analytics & Intelligence",
    short: "Measures, sourced answers and agents that propose, not act.",
    icon: "brain",
    headline: "Answers with sources. Recommendations with a human decision.",
    intro:
      "BunonBrain calculates consistent production and quality measures, answers questions from your own documents with citations, and uses focused agents to recommend actions that go through approval.",
    challenges: [
      { title: "Reports without action", body: "Numbers arrive, but what to do next is left to whoever reads them." },
      { title: "Knowledge locked in documents", body: "Procedures and buyer requirements are hard to find when they're needed." },
      { title: "Automation without accountability", body: "Systems that act on their own are not acceptable on a production floor." },
    ],
    outcomes: [
      { title: "Consistent measures", body: "The same formulas for every line and factory." },
      { title: "Cited answers", body: "Ask in plain language; see the passages behind each answer." },
      { title: "Accountable recommendations", body: "Agent proposals wait for an authorised decision and are audited." },
    ],
    modules: ["reports-analytics", "ask-and-knowledge", "ai-agents"],
    forRoles: ["Factory Manager", "Group management", "Compliance"],
  },
];

export const solutionBySlug = (slug: string) => SOLUTIONS.find((s) => s.slug === slug);
