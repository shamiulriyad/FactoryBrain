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
  | "factory" | "gauge" | "scan" | "file" | "check" | "message" | "bot" | "chart" | "shield" | "users" | "clock" | "plug" | "layers" | "brain" | "bell"
  | "camera" | "cpu";

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
    short: "Worker register, assignments, and a workforce hub on live records.",
    icon: "users",
    status: "available",
    group: "Workforce",
    summary:
      "Keep worker profiles in the same system as the lines they work on. Assign people to lines and workstations, link a worker to a login account, and see headcount, attendance, leave, overtime and performance for the factory in one Workforce hub.",
    problem:
      "Worker records, line assignments and skills usually sit in separate registers, so planners cannot see who is available or trained for an operation.",
    how: [
      { title: "Profiles", body: "HR and administrators create worker profiles one by one or by import, with employment details visible according to role." },
      { title: "Assignment", body: "Supervisors and managers assign workers to lines and workstations within their scope, with a full history." },
      { title: "One hub", body: "The Workforce hub shows headcount by department and line, today's attendance, leave, overtime and performance from the server's records." },
    ],
    beneficiaries: [
      { role: "HR / Attendance Officer", benefit: "One directory linked to the production structure." },
      { role: "Supervisors", benefit: "Know who is assigned to each workstation and who is in today." },
      { role: "Workers", benefit: "See their own record through a linked account." },
    ],
    capabilities: [
      { title: "Worker profiles", body: "Create, import and edit profiles, with employment details visible only to permitted roles." },
      { title: "Workstation assignment", body: "Move workers between lines and workstations in scope; every change is kept." },
      { title: "Account linking", body: "Connect a worker profile to a sign-in account." },
      { title: "Workforce hub", body: "Headcount, attendance today, leave records, overtime requests and workforce analytics together." },
    ],
    screenshot: { src: "/site/screens/workforce.jpg", alt: "BunonBrain Workforce hub with active workers, attendance, overtime awaiting approval and headcount by department and line" },
    related: ["attendance-shifts", "access-control"],
  },
  {
    slug: "attendance-shifts",
    name: "Attendance & Shift Management",
    short: "RFID check-in, shifts, breaks, leave and overtime approval on the server.",
    icon: "clock",
    status: "available",
    group: "Workforce",
    summary:
      "Server-side attendance, shifts, breaks, holidays, leave and overtime, governed by the same roles and scope as the rest of BunonBrain. Work dates, lateness and overtime follow each factory's time zone.",
    problem:
      "Attendance and overtime drive both payroll and compliance. They need to sit next to production data, under clear access rules, with every correction traceable.",
    how: [
      { title: "Punches", body: "Card readers (RFID) or authorised staff record check-ins, check-outs and breaks; each event keeps its source." },
      { title: "Shifts", body: "Shifts with their breaks are assigned to workers; lateness, early departure and over-break are calculated from them." },
      { title: "Approvals", body: "Overtime is requested and approved, corrections are reviewed, and leave is recorded as an approved document." },
    ],
    beneficiaries: [
      { role: "HR / Attendance Officer", benefit: "Attendance, leave and overtime in one controlled system." },
      { role: "Management", benefit: "Labour availability next to the production plan." },
      { role: "Supervisors", benefit: "Who is checked in, on break or late, line by line." },
    ],
    capabilities: [
      { title: "Attendance records", body: "Daily figures, sessions, missing punches and reports per factory and line." },
      { title: "Shifts and assignment", body: "Shift calendars with breaks, assigned to workers in bulk." },
      { title: "Overtime and corrections", body: "Request, approve and confirm payable overtime; review attendance corrections." },
      { title: "Holidays and leave", body: "Factory holidays and approved leave keep people from being counted absent." },
    ],
    related: ["workforce", "production-planning"],
  },
  {
    slug: "monitoring-camera-ai",
    name: "Monitoring Room & Camera AI",
    short: "Cameras, devices, events and alerts live, with AI analysis of camera frames.",
    icon: "camera",
    status: "available",
    group: "Operations",
    summary:
      "A central monitoring room for the cameras, devices and alerts of the factories and lines a person is responsible for. Camera frames can be analysed by AI for people, activity, helmets and safety vests, and confirmed changes become events and alerts.",
    problem:
      "Cameras record, but nobody watches every screen. Problems are found later on the recording, if at all.",
    how: [
      { title: "Live feed", body: "Events and alerts arrive in real time on every open screen, newest first and in local time." },
      { title: "Camera AI", body: "A browser camera's frames are analysed on the server by a vision model; frames are not stored." },
      { title: "Act", body: "Alerts are acknowledged, escalated or resolved, and every action is kept." },
    ],
    beneficiaries: [
      { role: "Monitoring Operator", benefit: "One screen for cameras, devices and alerts." },
      { role: "Factory Manager", benefit: "Safety issues raised as alerts, not discovered later." },
    ],
    capabilities: [
      { title: "Monitoring room", body: "Cameras, devices, live events and active alerts across your scope." },
      { title: "Camera AI", body: "People, activity, helmet and safety-vest checks with approximate boxes. No face recognition." },
      { title: "Alert workflow", body: "Acknowledge, escalate, resolve or dismiss, with history." },
      { title: "Simulated data labelled", body: "Simulated events are marked as such everywhere they appear." },
    ],
    screenshot: { src: "/site/screens/monitoring.jpg", alt: "BunonBrain monitoring room with cameras, devices, active alerts and a live event feed (simulated events labelled)" },
    related: ["machine-monitoring", "line-monitoring"],
  },
  {
    slug: "machine-monitoring",
    name: "Machine Monitoring (IoT)",
    short: "Running, idle, stopped or in error, from sensors over MQTT, with piece counts.",
    icon: "cpu",
    status: "available",
    group: "Operations",
    summary:
      "ESP32 sensors and piece counters report over MQTT or HTTPS. BunonBrain derives each machine's state, records downtime and counts production, and flags machines that stop or go offline.",
    problem:
      "A stopped machine is noticed on a floor walk, and output is counted by hand at the end of the hour.",
    how: [
      { title: "Connect", body: "Register sensors and counters as devices; they send readings over MQTT (preferred) or HTTPS." },
      { title: "Derive", body: "Readings become machine states (running, idle, stopped, error, offline) and production counts." },
      { title: "Report", body: "Uptime, downtime and counts feed the line view, analytics and planned-versus-actual." },
    ],
    beneficiaries: [
      { role: "Production Supervisor", benefit: "Which machines stopped, and since when." },
      { role: "Maintenance", benefit: "Errors and downtime history per machine." },
      { role: "Planning", benefit: "Actual output from counters, not tallies." },
    ],
    capabilities: [
      { title: "Machine states", body: "Running, idle, stopped, error, maintenance and offline, with reasons." },
      { title: "Piece counters", body: "Production counts with resets, corrections and duplicate handling." },
      { title: "Device health", body: "Heartbeats and online / offline status per device." },
      { title: "IoT simulator", body: "Evaluate the whole flow before any hardware is installed; simulated data is labelled." },
    ],
    related: ["monitoring-camera-ai", "integrations", "production-planning"],
  },
  {
    slug: "floor-map-brain",
    name: "Floor Map & Factory Brain",
    short: "Every floor's live status, and a map of how lines, machines and orders connect.",
    icon: "layers",
    status: "available",
    group: "Intelligence",
    summary:
      "The floor map shows each production floor with its people, machines, open alerts and an OK / Watch / Act status. Factory Brain draws the factory as a graph: floors, lines, machines, open orders, buyers, styles, targets and open risks.",
    problem:
      "Managers see one screen per module and have to connect the dots themselves.",
    how: [
      { title: "Roll up", body: "Alerts, machine states and exceptions are rolled up per floor; only real data sets a status." },
      { title: "Connect", body: "The graph links each order to its buyer, style and lines, and each risk to what it threatens." },
      { title: "Open", body: "Every node and floor opens the record behind it." },
    ],
    beneficiaries: [
      { role: "Factory Manager", benefit: "The whole building at a glance." },
      { role: "Planners", benefit: "See which orders depend on a line with a problem." },
    ],
    capabilities: [
      { title: "Live floor map", body: "Floors stacked like the building, with status, people, machines and alerts." },
      { title: "Floor pages", body: "Lines, machine states and recent alerts for one floor." },
      { title: "Factory graph", body: "Floors, lines, machines, orders, buyers, styles, targets and risks, filterable by kind." },
    ],
    screenshot: { src: "/site/screens/floor-map.jpg", alt: "BunonBrain live floor map with three production floors, checked-in workers, machines, open alerts and cameras" },
    related: ["line-monitoring", "machine-monitoring", "ai-agents"],
  },
  {
    slug: "document-automation",
    name: "Document Automation",
    short: "38 factory documents generated from your data, exported to PDF, Excel or Word.",
    icon: "file",
    status: "available",
    group: "Platform",
    summary:
      "Factory documents such as AQL reports, GRNs, packing lists and attendance sheets are generated from the records already in BunonBrain, checked for missing values, reviewed and approved, and exported to PDF, Excel or Word.",
    problem:
      "Clerks retype the same figures into many forms, and buyers and auditors receive documents nobody can trace back to a record.",
    how: [
      { title: "Generate", body: "Pick a document type and period; values are filled from the factory's records." },
      { title: "Check", body: "Missing values, errors and warnings are listed before submission." },
      { title: "Approve and export", body: "Documents go through review and approval and are exported in the format the reader needs." },
    ],
    beneficiaries: [
      { role: "Document Controller", benefit: "One register of every document and its status." },
      { role: "Compliance", benefit: "Each figure traceable to a record." },
    ],
    capabilities: [
      { title: "38 document types", body: "Production, quality, store, HR and compliance documents." },
      { title: "Review and approval", body: "Versioned documents with a review and approval workflow." },
      { title: "PDF, Excel, Word", body: "Exports in the format the reader needs." },
    ],
    related: ["merchandising", "reports-analytics"],
  },
  {
    slug: "integrations",
    name: "Data Sources & Integrations",
    short: "MQTT and HTTPS ingest, file imports, and every source's status on one page.",
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
      { title: "MQTT and HTTPS ingest", body: "Machine sensors, piece counters, energy meters and RFID readers over MQTT (preferred) or HTTPS." },
      { title: "File imports", body: "Output import and document uploads." },
      { title: "Simulator", body: "A built-in event stream for demonstrations and evaluation." },
      { title: "Integration status", body: "Every source with its devices, record counts and last activity, on one page." },
      { title: "Direct PLC / Modbus connectors", body: "Not included today.", status: "planned" },
    ],
    screenshot: { src: "/site/screens/integrations.jpg", alt: "BunonBrain integrations page listing RFID readers, attendance card readers, machine sensors and production counters with their devices and records" },
    related: ["machine-monitoring", "line-monitoring", "merchandising"],
  },
];

export const moduleBySlug = (slug: string) => MODULES.find((m) => m.slug === slug);

export const STATUS_LABEL: Record<ModuleStatus, string> = {
  available: "Available",
  demo: "Demo preview",
  planned: "Planned",
};
