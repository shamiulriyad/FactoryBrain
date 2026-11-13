/**
 * Product modules shown on /product and /product/[slug]. Every statement here
 * describes what the BunonBrain application actually does today (see
 * README.md, ARCHITECTURE.md and ACCESS_CONTROL.md). Capabilities that are
 * not built yet carry status "planned"; browser-only demonstrations carry
 * status "demo".
 */

export type ModuleStatus = "available" | "demo" | "planned";

export interface ModuleCapability {
  title: string;
  body: string;
  status?: ModuleStatus;
}

export interface ProductModule {
  slug: string;
  name: string;
  /** One line for menus and cards. */
  short: string;
  icon: IconName;
  status: ModuleStatus;
  group: "Operations" | "Production" | "Intelligence" | "Platform" | "Workforce";
  summary: string;
  problem: string;
  how: { title: string; body: string }[];
  beneficiaries: { role: string; benefit: string }[];
  capabilities: ModuleCapability[];
  /** Real product screenshot under /public/site/screens (demo data). */
  screenshot?: { src: string; alt: string };
  notes?: string[];
  related: string[];
}

export type IconName =
  | "factory" | "gauge" | "scan" | "file" | "check" | "message" | "bot" | "chart" | "shield" | "users" | "clock" | "plug" | "layers" | "brain" | "bell";

export const MODULES: ProductModule[] = [
  {
    slug: "production-planning",
    name: "Production Planning",
    short: "Orders, approval-gated plans, output and line efficiency.",
    icon: "factory",
    status: "available",
    group: "Production",
    summary:
      "Plan sewing output against buyer orders, record what each line actually produced, and see efficiency, work in progress and delivery risk calculated from the plant's own records.",
    problem:
      "Most plants plan in spreadsheets that drift from what happens on the floor. Planned and actual output are compared days later, efficiency is estimated rather than calculated, and a late order is noticed when it is already late.",
    how: [
      { title: "Set up the plant", body: "An administrator adds lines, shifts and styles with their SMV. Nothing is pre-seeded: the plan starts from your own data." },
      { title: "Plan and approve", body: "Planners draft a production plan per order and line. A plan is submitted for approval and only becomes active after an authorised manager approves it." },
      { title: "Record output", body: "Hourly or shift output is recorded on screen or imported from a file. Corrections are kept as corrections, so the original entry is never silently overwritten." },
      { title: "Compare and act", body: "Planned versus actual, line efficiency from SMV, WIP and order-level delivery risk update from the recorded output." },
    ],
    beneficiaries: [
      { role: "Planning & IE", benefit: "One plan of record, with time studies and line-balancing proposals built from observations." },
      { role: "Factory Manager", benefit: "Approves plans and sees which orders are at risk before the ex-factory date." },
      { role: "Production Supervisor", benefit: "Sees targets and recorded output for the lines they are assigned to." },
    ],
    capabilities: [
      { title: "Orders and styles", body: "Buyer orders with quantities and dates, styles with SMV, linked to the lines that make them." },
      { title: "Approval-gated plans", body: "Draft, submit, approve, activate, complete or cancel, with every change request resolved explicitly." },
      { title: "Output recording and import", body: "Manual entry and file import, with an audit-friendly correction flow." },
      { title: "Efficiency from SMV", body: "Line efficiency calculated from produced minutes against available minutes, not typed in." },
      { title: "WIP and delivery risk", body: "Work in progress per line and a risk view for each order against its delivery date." },
      { title: "Time study and line balancing", body: "Record operation observations, find the bottleneck and generate a balancing proposal that goes through approval before it is applied." },
    ],
    related: ["line-monitoring", "approvals-workflow", "reports-analytics"],
  },
  {
    slug: "line-monitoring",
    name: "Line Board & Floor Alerts",
    short: "Live line status, sensor readings and alerts for the floor.",
    icon: "gauge",
    status: "available",
    group: "Operations",
    summary:
      "A shared view of every sewing line: throughput, efficiency against target, bottlenecks and machine health, with floor alerts pushed to everyone watching in real time.",
    problem:
      "Supervisors learn about a stopped machine or a growing pile of bundles by walking the floor. By the time the information reaches the office, the hour is lost.",
    how: [
      { title: "Collect signals", body: "Machine, energy and bundle readings arrive through the sensor ingest API. Without hardware, a built-in simulator produces a realistic event stream for evaluation." },
      { title: "Compute the board", body: "The line board turns readings and recorded output into line status, efficiency and flags." },
      { title: "Broadcast alerts", body: "Alerts are stored by the server and sent to every open screen over a live channel, so acknowledging one updates it for everyone." },
    ],
    beneficiaries: [
      { role: "Monitoring Operator", benefit: "Watches the board and reviews alerts as they arrive." },
      { role: "Production Supervisor", benefit: "Sees which of their lines needs attention first." },
      { role: "Factory Manager", benefit: "Starts the day with a short morning brief instead of a long report." },
    ],
    capabilities: [
      { title: "Line status board", body: "Efficiency, target gap, WIP and non-productive time for each line, flagged as on track, watch or critical." },
      { title: "Floor alerts in real time", body: "Server-stored alerts broadcast over SignalR; read state is shared across the team." },
      { title: "Sensor ingest", body: "An API for machine telemetry, energy and RFID bundle scans, with safety limits on what it accepts." },
      { title: "Floor map", body: "A per-floor view of lines and machines." },
      { title: "Morning brief", body: "A generated summary of the top issues, pending decisions and risks for the day." },
    ],
    screenshot: { src: "/site/screens/overview.jpg", alt: "BunonBrain overview with line status board and floor status, demo data" },
    notes: ["Hardware integrations (PLC, Modbus, MQTT) are not included; readings arrive through the ingest API."],
    related: ["production-planning", "ai-agents", "integrations"],
  },
  {
    slug: "quality-inspection",
    name: "Quality Inspection",
    short: "Photo-based defect detection with inspector review and DHU.",
    icon: "scan",
    status: "available",
    group: "Production",
    summary:
      "Inspectors upload garment photos, a configurable vision model marks likely defects, and the inspector confirms or corrects every finding. Defect rates and DHU are calculated only from reviewed records.",
    problem:
      "Defect data is usually collected on paper and summarised weekly. It is hard to trace a defect back to a line and an hour, and impossible to check what an automated system claimed.",
    how: [
      { title: "Capture", body: "An inspector uploads a photo of the garment with its line and style." },
      { title: "Analyse", body: "If a vision provider is configured, the image is analysed and likely defects are drawn as boxes. If not, the inspection is stored and marked for manual review; nothing is predicted." },
      { title: "Review", body: "The inspector accepts, rejects or corrects each finding and adds any that were missed." },
      { title: "Measure", body: "Defect rates and DHU are calculated from reviewed inspections only." },
    ],
    beneficiaries: [
      { role: "QC Inspector", benefit: "Faster documentation, with the final say on every finding." },
      { role: "Quality Manager", benefit: "Trends by line and defect category that can be trusted." },
      { role: "Factory Manager", benefit: "A quality picture tied to the lines that produced it." },
    ],
    capabilities: [
      { title: "Configurable vision provider", body: "Use the configured language model's vision capability or your own detection service through a documented HTTP contract." },
      { title: "Human review of every finding", body: "AI output is a suggestion until an inspector reviews it." },
      { title: "Defect taxonomy", body: "Administrators maintain the factory's own defect categories." },
      { title: "History and metrics", body: "Inspection history, defect rates and DHU from reviewed records." },
    ],
    screenshot: { src: "/site/screens/qc.jpg", alt: "Quality inspection screen in BunonBrain, demo data" },
    related: ["production-planning", "reports-analytics", "ai-agents"],
  },
  {
    slug: "merchandising",
    name: "Merchandising & Documents",
    short: "Buyer orders, milestones and versioned order documents.",
    icon: "file",
    status: "available",
    group: "Production",
    summary:
      "Keep buyer orders, their milestones and every purchase order, tech pack and spec sheet in one place. Values are extracted from the documents with the quoted source, merchandisers confirm them, and conflicts between documents are flagged.",
    problem:
      "Order details live in email attachments and personal folders. When a buyer revises a PO, nobody is sure which version the floor is working from, or which quantity is correct.",
    how: [
      { title: "Upload", body: "Attach POs, tech packs and spec sheets (PDF, DOCX, CSV, text) to an order. New revisions are stored as versions." },
      { title: "Extract", body: "Values such as quantities, dates and sizes are extracted with the text they came from. By default a rule-based parser does this; a language model can be enabled instead." },
      { title: "Confirm", body: "A merchandiser corrects and confirms extracted values before they are used." },
      { title: "Resolve", body: "When two documents disagree, the conflict is flagged until someone resolves it." },
    ],
    beneficiaries: [
      { role: "Merchandiser", benefit: "One record per order, with the source of every value." },
      { role: "Planning", benefit: "Quantities and dates that have been confirmed, not copied." },
      { role: "Management", benefit: "Deadlines and missing information visible across orders." },
    ],
    capabilities: [
      { title: "Orders and milestones", body: "Buyer orders with milestone tracking and deadlines." },
      { title: "Versioned documents", body: "Every upload kept as a version, with the original file stored." },
      { title: "Sourced extraction", body: "Each extracted value shows the text it came from." },
      { title: "Conflict detection", body: "Disagreements between documents are flagged and resolved explicitly." },
    ],
    notes: ["There is no OCR for scanned images and no email integration."],
    related: ["production-planning", "approvals-workflow"],
  },
  {
    slug: "approvals-workflow",
    name: "Approvals & Workflow",
    short: "Risk-tiered decisions, checked by the server and audited.",
    icon: "check",
    status: "available",
    group: "Platform",
    summary:
      "Risky actions wait in an approval queue instead of happening automatically. The server checks the approver's role and the risk tier, requires a reason to reject, and writes every decision to an append-only audit log.",
    problem:
      "When software can change a plan or move people between lines, someone accountable has to agree first, and there must be a record of who decided what and why.",
    how: [
      { title: "Propose", body: "Agents, production plans and line-balancing proposals create approval requests with a risk tier." },
      { title: "Decide", body: "An authorised person approves or rejects. High-risk items need a higher permission, and rejection needs a reason." },
      { title: "Record", body: "Each decision is written to the audit log in the same transaction. The audit table rejects edits and deletes at the database level." },
    ],
    beneficiaries: [
      { role: "Factory Manager", benefit: "One queue of decisions, ordered by risk." },
      { role: "Administrators", benefit: "A tamper-resistant record for internal review." },
    ],
    capabilities: [
      { title: "Risk tiers", body: "Low, medium and high risk, with separate permissions for high-risk decisions." },
      { title: "Server-side enforcement", body: "Permissions are checked by the API on every request, not just hidden in the interface." },
      { title: "Append-only audit log", body: "Decisions, role changes and settings changes are recorded and cannot be edited through the application." },
    ],
    screenshot: { src: "/site/screens/approvals.jpg", alt: "Approvals queue in BunonBrain, demo data" },
    related: ["access-control", "ai-agents", "production-planning"],
  },
  {
    slug: "ask-and-knowledge",
    name: "Ask BunonBrain & Knowledge Base",
    short: "Answers from your manuals and policies, with sources.",
    icon: "message",
    status: "available",
    group: "Intelligence",
    summary:
      "Ask questions in plain language and get answers drawn from the factory's manuals, procedures and policies, with the source passages cited so every answer can be checked.",
    problem:
      "Procedures and buyer requirements are spread across documents few people read. Supervisors ask the same questions repeatedly, and answers depend on who is asked.",
    how: [
      { title: "Ingest", body: "Administrators add documents to the knowledge base. They are split into passages and indexed." },
      { title: "Retrieve", body: "Each question is matched against the index with hybrid search: vector similarity and keyword (BM25) ranking." },
      { title: "Answer with sources", body: "A language model writes the answer from the retrieved passages and cites them. With no model configured, the system runs in a clearly labelled demo mode." },
    ],
    beneficiaries: [
      { role: "Supervisors", benefit: "Quick, sourced answers on the floor." },
      { role: "Compliance & HR", benefit: "Policies that are actually consulted." },
      { role: "Administrators", benefit: "Control over which provider and model are used." },
    ],
    capabilities: [
      { title: "Hybrid retrieval", body: "Vector and BM25 search combined for better recall on technical terms." },
      { title: "Cited answers", body: "Every answer lists the passages it used." },
      { title: "Choice of provider", body: "OpenAI, Anthropic or Google Gemini, configured by an administrator; API keys are stored encrypted." },
      { title: "Controlled reindexing", body: "Changing embedding settings shows a plan and cost first and runs only after confirmation." },
    ],
    related: ["ai-agents", "reports-analytics"],
  },
  {
    slug: "ai-agents",
    name: "Agents & Insights",
    short: "Focused AI agents that propose actions for approval.",
    icon: "bot",
    status: "available",
    group: "Intelligence",
    summary:
      "Specialised agents analyse line efficiency and machine uptime, and an orchestrator combines their findings. Their output becomes insights and, where action is needed, approval requests; agents never act on their own.",
    problem:
      "Dashboards show numbers but rarely say what to do next. Automated systems that act without oversight are not acceptable on a production floor.",
    how: [
      { title: "Analyse", body: "The Line Efficiency Agent and the Maintenance & Uptime Agent read line and machine data." },
      { title: "Combine", body: "The Manager Orchestrator Agent ranks findings by what they put at risk." },
      { title: "Propose", body: "Recommended actions go to the approval queue with a risk tier." },
    ],
    beneficiaries: [
      { role: "Factory Manager", benefit: "Ranked recommendations instead of raw numbers." },
      { role: "Supervisors", benefit: "Early warning on efficiency drops and machine trouble." },
    ],
    capabilities: [
      { title: "Line efficiency analysis", body: "Finds lines falling behind target and likely causes." },
      { title: "Maintenance and uptime", body: "Flags machines at risk from their readings." },
      { title: "Human in the loop", body: "Every proposed action passes through approvals." },
    ],
    related: ["approvals-workflow", "line-monitoring", "ask-and-knowledge"],
  },
  {
    slug: "reports-analytics",
    name: "Reports & Analytics",
    short: "Efficiency, quality and delivery measures from recorded data.",
    icon: "chart",
    status: "available",
    group: "Intelligence",
    summary:
      "Measures that managers and buyers ask about, calculated from the records the plant keeps in BunonBrain: planned versus actual, line efficiency, WIP, defect rates and DHU, and delivery risk.",
    problem:
      "Reports assembled by hand each week are late, inconsistent between people, and hard to trace back to source.",
    how: [
      { title: "Record once", body: "Output, inspections and orders are recorded in their own modules." },
      { title: "Calculate consistently", body: "Measures use documented formulas, the same way every time." },
      { title: "Scope by role", body: "Each person sees the factories and lines their role allows." },
    ],
    beneficiaries: [
      { role: "Management", benefit: "Consistent numbers across lines and factories." },
      { role: "Buyers' audits", benefit: "Figures that trace back to individual records." },
    ],
    capabilities: [
      { title: "Production measures", body: "Planned vs actual, efficiency from SMV, WIP, non-productive time." },
      { title: "Quality measures", body: "Defect rates and DHU from reviewed inspections." },
      { title: "Delivery risk", body: "Order-level risk against delivery dates." },
    ],
    related: ["production-planning", "quality-inspection", "line-monitoring"],
  },
  {
    slug: "access-control",
    name: "Role-Based Access Control",
    short: "Eight roles, factory and line scope, checked on every request.",
    icon: "shield",
    status: "available",
    group: "Platform",
    summary:
      "People see and do only what their job requires. Eight system roles grant module permissions, scoped to a factory and, for supervisors, to their assigned lines. The API checks a permission on every request.",
    problem:
      "In a multi-factory group, a supervisor should not see another factory's data, and a worker should not see colleagues' records. Hiding buttons is not enough.",
    how: [
      { title: "Assign roles", body: "Administrators give each person one or more roles: Super Admin, Factory Admin, Factory Manager, Production Supervisor, Monitoring Operator, HR / Attendance Officer, IT / Device Administrator or Worker." },
      { title: "Scope", body: "Accounts are tied to factories, and supervisors to production lines." },
      { title: "Enforce", body: "Every API endpoint declares the permission it needs; requests without it are refused." },
    ],
    beneficiaries: [
      { role: "Group management", benefit: "Separate factories in one system." },
      { role: "IT & administrators", benefit: "Clear, auditable access rules." },
      { role: "Workers", benefit: "Access to their own information only." },
    ],
    capabilities: [
      { title: "Eight system roles", body: "From platform administration to individual workers." },
      { title: "Factory and line scope", body: "Data is partitioned by factory and line assignment." },
      { title: "Session security", body: "Short-lived access tokens kept in memory and an HttpOnly refresh cookie; deactivating a user ends their sessions immediately." },
      { title: "Audit of access changes", body: "Role and account changes are written to the audit log." },
    ],
    related: ["approvals-workflow", "workforce"],
  },
  {
    slug: "workforce",
    name: "Workforce Management",
    short: "Worker profiles and workstation assignment; HR modules in demo.",
    icon: "users",
    status: "available",
    group: "Workforce",
    summary:
      "Keep worker profiles in the same system as the lines they work on. Assign people to lines and workstations, link a worker to a login account, and preview the HR and wellbeing modules on demo data.",
    problem:
      "Worker records, line assignments and skills usually sit in separate registers, so planners cannot see who is available or trained for an operation.",
    how: [
      { title: "Profiles", body: "HR and administrators create worker profiles with employment details, visible according to role." },
      { title: "Assignment", body: "Supervisors and managers assign workers to lines and workstations within their scope." },
      { title: "Self-service", body: "A worker linked to a login sees their own profile." },
    ],
    beneficiaries: [
      { role: "HR / Attendance Officer", benefit: "One directory linked to the production structure." },
      { role: "Supervisors", benefit: "Know who is assigned to each workstation." },
      { role: "Workers", benefit: "See their own record." },
    ],
    capabilities: [
      { title: "Worker profiles", body: "Create and edit profiles, with employment details visible only to permitted roles." },
      { title: "Workstation assignment", body: "Move workers between lines and workstations in scope." },
      { title: "Account linking", body: "Connect a worker profile to a sign-in account." },
      { title: "Roll-call, leave, overtime, skills and wellbeing", body: "Shown as an interactive demonstration on sample data stored only in the browser.", status: "demo" },
    ],
    screenshot: { src: "/site/screens/workforce.jpg", alt: "Workforce, HR and wellbeing demo in BunonBrain, running on browser-only sample data" },
    notes: ["The HR, attendance and wellbeing screens currently run on demo data in the browser and do not save to the server."],
    related: ["attendance-shifts", "access-control"],
  },
  {
    slug: "attendance-shifts",
    name: "Attendance & Shift Management",
    short: "Attendance, leave and overtime records on the server.",
    icon: "clock",
    status: "planned",
    group: "Workforce",
    summary:
      "Server-side attendance, shift assignment, leave and overtime, governed by the same roles and scope as the rest of BunonBrain. The permissions for these functions are already defined; the module itself is in development.",
    problem:
      "Attendance and overtime drive both payroll and compliance. They need to sit next to production data, under clear access rules.",
    how: [
      { title: "Shifts", body: "Production shifts already exist in planning; attendance will attach people to them." },
      { title: "Records", body: "Attendance, leave and overtime will be stored on the server with role-based access." },
      { title: "Review", body: "HR officers will manage records; managers will see summaries for their scope." },
    ],
    beneficiaries: [
      { role: "HR / Attendance Officer", benefit: "Attendance and overtime in one controlled system." },
      { role: "Management", benefit: "Labour availability next to the production plan." },
    ],
    capabilities: [
      { title: "Attendance records", body: "View and manage attendance.", status: "planned" },
      { title: "Shift assignment", body: "Assign workers to shifts.", status: "planned" },
      { title: "Leave and overtime", body: "Request, approve and report.", status: "planned" },
    ],
    notes: ["This module is planned. Dates are not committed. A browser-only preview is available in the Workforce demo."],
    related: ["workforce", "production-planning"],
  },
  {
    slug: "integrations",
    name: "Data Sources & Integrations",
    short: "Sensor ingest, file imports and documents.",
    icon: "plug",
    status: "available",
    group: "Platform",
    summary:
      "BunonBrain works from the data the plant already has. Sensor readings arrive through an ingest API, output can be imported from files, and documents feed merchandising and the knowledge base.",
    problem:
      "New systems that demand new hardware and new habits stall. Adoption works when the first step uses what the factory already records.",
    how: [
      { title: "Start with files", body: "Import output and upload documents with no extra hardware." },
      { title: "Add readings", body: "Send machine, energy or RFID readings to the ingest API when ready." },
      { title: "Evaluate safely", body: "A simulator generates realistic readings so the system can be explored before any hardware is connected." },
    ],
    beneficiaries: [
      { role: "IT / Device Administrator", benefit: "A documented ingest contract with safety limits." },
      { role: "Management", benefit: "Start small, add sources over time." },
    ],
    capabilities: [
      { title: "Sensor ingest API", body: "Machine telemetry, energy meters and RFID bundle scans." },
      { title: "File imports", body: "Output import and document uploads." },
      { title: "Simulator", body: "A built-in event stream for demonstrations and evaluation." },
      { title: "Direct PLC / Modbus / MQTT connectors", body: "Not included today.", status: "planned" },
    ],
    related: ["line-monitoring", "merchandising", "ask-and-knowledge"],
  },
];

export const moduleBySlug = (slug: string) => MODULES.find((m) => m.slug === slug);

export const STATUS_LABEL: Record<ModuleStatus, string> = {
  available: "Available",
  demo: "Demo preview",
  planned: "Planned",
};
