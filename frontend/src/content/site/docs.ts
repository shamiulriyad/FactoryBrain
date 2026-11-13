import type { Block } from "./blocks";

/**
 * Public product documentation. Written from the application itself
 * (README.md, ACCESS_CONTROL.md, ARCHITECTURE.md). Keep in step with the
 * product: if a behaviour changes, update the page here.
 */
export interface Doc {
  slug: string;
  title: string;
  summary: string;
  section: "Getting started" | "Modules" | "Administration";
  body: Block[];
}

export const DOCS: Doc[] = [
  {
    slug: "getting-started",
    title: "Getting started",
    summary: "Sign in, find your way around and understand what you can see.",
    section: "Getting started",
    body: [
      { t: "h2", text: "Signing in" },
      { t: "p", text: "Your administrator creates your account and gives you a temporary password. Sign in at **/login**. If you are asked to change the password at first sign-in, choose one you don't use anywhere else." },
      { t: "p", text: "Sessions use a short-lived access token kept in the browser's memory and a refresh cookie. If you are inactive for a while you may need to sign in again; if an administrator deactivates your account, your sessions end immediately." },
      { t: "h2", text: "Finding your way" },
      { t: "ul", items: ["The sidebar lists the modules your role can open; items you can't use are not shown.", "Use the factory switcher at the top of the sidebar if you belong to more than one factory.", "Search lines, orders and alerts with **Ctrl K**.", "Switch between English and বাংলা, and between light and dark themes, from the top bar."] },
      { t: "h2", text: "Simulated and demo data" },
      { t: "p", text: "Screens that show simulated or demo data say so with a label. The Workforce module's HR, attendance and wellbeing screens run on browser-only sample data and do not save to the server." },
    ],
  },
  {
    slug: "roles-and-permissions",
    title: "Roles and permissions",
    summary: "The eight system roles, what each one can reach and how scope works.",
    section: "Administration",
    body: [
      { t: "p", text: "Access is role-based. Each account has one or more roles, and each role grants module permissions. The API checks the required permission on every request." },
      { t: "h2", text: "The eight roles" },
      { t: "table", head: ["Role", "Reach", "Main responsibilities"], rows: [
        ["Super Admin", "All factories", "Factories, Factory Admins, platform settings, global audit"],
        ["Factory Admin", "Assigned factories", "Users and roles, lines, departments, factory settings"],
        ["Factory Manager", "Assigned factories", "Operations oversight, production, approvals, reports"],
        ["Production Supervisor", "Assigned lines", "Workers and workstations on their lines, production, line alerts"],
        ["Monitoring Operator", "Assigned factories", "Monitoring dashboard, acknowledging and escalating alerts"],
        ["HR / Attendance Officer", "Assigned factories", "Worker records; attendance, shifts, leave and overtime (planned)"],
        ["IT / Device Administrator", "Assigned factories", "Devices, integrations, technical logs"],
        ["Worker", "Own records", "Own profile and workstation"],
      ] },
      { t: "h2", text: "Scope" },
      { t: "ul", items: ["Most roles are scoped to the factories they are assigned to.", "Production Supervisors are additionally scoped to production lines.", "Worker profiles are separate from login accounts; a worker can be linked to at most one account to see their own record."] },
      { t: "h2", text: "Audit" },
      { t: "p", text: "Role assignments, account changes, settings changes and approval decisions are written to an append-only audit log, readable by roles with the audit permission." },
    ],
  },
  {
    slug: "production-planning",
    title: "Production planning",
    summary: "Set up lines, plan against orders, record output and read efficiency and risk.",
    section: "Modules",
    body: [
      { t: "h2", text: "Setup" },
      { t: "p", text: "Before anything is calculated, an administrator adds production lines, shifts (the working calendar) and styles with their SMV under **Production → Setup**. Nothing is pre-filled." },
      { t: "h2", text: "Plans and approval" },
      { t: "ol", items: ["Create a plan for an order and a line.", "Submit it for approval.", "An authorised manager approves it; it can then be activated.", "Plans can be completed, withdrawn or cancelled; change requests are resolved explicitly."] },
      { t: "h2", text: "Recording output" },
      { t: "p", text: "Record output on screen or import it from a CSV file. Mistakes are fixed with a correction, so the original entry is kept." },
      { t: "h2", text: "Measures" },
      { t: "ul", items: ["Planned versus actual output", "Line efficiency from SMV and available minutes", "Work in progress per line", "Delivery risk per order"] },
      { t: "h2", text: "Line balancing" },
      { t: "p", text: "Record time-study observations, run the analysis to find the bottleneck, and submit the generated proposal for approval before applying it." },
      { t: "note", text: "Production dates follow the plant's time zone, set by the PRODUCTION_TIME_ZONE setting of the deployment." },
    ],
  },
  {
    slug: "quality-inspection",
    title: "Quality inspection",
    summary: "Upload garment photos, review AI findings and read quality measures.",
    section: "Modules",
    body: [
      { t: "h2", text: "Creating an inspection" },
      { t: "p", text: "Choose **New inspection**, add the garment photo (JPEG, PNG or WebP) and the product, stage and line. Uploads are checked: a file whose contents don't match an image type is rejected." },
      { t: "h2", text: "Analysis" },
      { t: "p", text: "If the deployment has a vision provider configured, the photo is analysed and likely defects are drawn as boxes with a confidence. Without one, the inspection is stored and marked for manual review; nothing is predicted. Failed analyses can be retried." },
      { t: "h2", text: "Review" },
      { t: "p", text: "An inspector accepts, rejects or corrects each finding and records the outcome. Only reviewed inspections count towards defect rates and DHU." },
      { t: "h2", text: "Defect taxonomy" },
      { t: "p", text: "Administrators maintain the factory's defect categories under **Quality → Defect taxonomy**." },
    ],
  },
  {
    slug: "merchandising-documents",
    title: "Merchandising and documents",
    summary: "Register buyer orders, upload documents and confirm extracted values.",
    section: "Modules",
    body: [
      { t: "h2", text: "Orders and milestones" },
      { t: "p", text: "Register a buyer order with its quantities and dates, and track milestones against deadlines." },
      { t: "h2", text: "Documents" },
      { t: "p", text: "Upload POs, tech packs and spec sheets (PDF, DOCX, CSV or text). A new upload for the same document is stored as a new version; the original file is kept." },
      { t: "h2", text: "Extraction and confirmation" },
      { t: "p", text: "By default a rule-based parser reads labelled values (for example \"PO No: …\") and colour × size tables, and quotes the line each value came from. It is not AI and does not understand unlabelled text. A language model can be enabled instead by the deployment. Extracted values become order data only when a merchandiser confirms them." },
      { t: "h2", text: "Conflicts" },
      { t: "p", text: "When two documents disagree on a value, a conflict is raised and stays open until someone resolves it." },
      { t: "note", text: "There is no OCR for scanned images and no email integration." },
    ],
  },
  {
    slug: "approvals-and-audit",
    title: "Approvals and the audit log",
    summary: "How recommendations are decided and recorded.",
    section: "Modules",
    body: [
      { t: "p", text: "Agents, vision analyses, production plans and line-balancing proposals create approval requests. Each has a risk tier: low, medium or high." },
      { t: "ul", items: ["Deciding requires the approvals permission; high-risk items need the high-risk permission.", "Rejecting requires a reason.", "Decisions are final and are written to the audit log in the same transaction."] },
      { t: "h2", text: "The audit log" },
      { t: "p", text: "The audit log records who did what, when, to which item, with the previous and new state and any reason. The database rejects edits and deletions of audit entries." },
    ],
  },
  {
    slug: "ask-and-knowledge",
    title: "Ask BunonBrain and the knowledge base",
    summary: "Ask questions about your documents and get cited answers.",
    section: "Modules",
    body: [
      { t: "p", text: "Ask BunonBrain answers questions from the documents in the knowledge base and lists the passages it used." },
      { t: "h2", text: "How answers are produced" },
      { t: "ol", items: ["Your question is matched against the indexed documents with vector and keyword (BM25) search.", "The most relevant passages are given to the configured language model.", "The answer is returned with citations."] },
      { t: "h2", text: "Demo mode" },
      { t: "p", text: "If no language model is configured, Ask runs in demo mode and returns clearly labelled canned answers." },
      { t: "h2", text: "Managing the knowledge base" },
      { t: "p", text: "Platform administrators ingest documents and rebuild the index. Changing embedding settings shows a plan with the number of documents, vector size and cost, and runs only after confirmation." },
    ],
  },
  {
    slug: "ai-settings",
    title: "AI providers and settings",
    summary: "Choose the language model provider and manage API keys.",
    section: "Administration",
    body: [
      { t: "p", text: "A platform administrator chooses the provider, model and API key under **Settings → Advanced**. Supported providers are OpenAI, Anthropic and Google Gemini." },
      { t: "ul", items: ["Saved settings are stored in the database with the key encrypted, survive restarts and take effect from the next question.", "Embeddings are configured separately; changing the language model never changes them.", "Clearing the settings switches Ask and agents to demo mode."] },
      { t: "note", text: "When a provider is configured, the question and the retrieved passages are sent to that provider to generate the answer. Choose a provider whose terms suit your data." },
    ],
  },
  {
    slug: "sensors-and-integrations",
    title: "Sensors and data sources",
    summary: "Send machine, energy and RFID readings, and use the simulator.",
    section: "Administration",
    body: [
      { t: "p", text: "Readings reach BunonBrain through the sensor ingest API, which enforces limits on what it accepts. Machine telemetry, energy meters and RFID bundle scans are supported." },
      { t: "h2", text: "Simulator" },
      { t: "p", text: "With the simulator enabled, the API generates a realistic stream of readings for demonstrations. Simulated data is labelled in the interface." },
      { t: "h2", text: "Not included" },
      { t: "p", text: "There are no direct PLC, Modbus or MQTT connectors. Devices or gateways send readings to the ingest API." },
    ],
  },
  {
    slug: "account-and-security",
    title: "Your account and security",
    summary: "Passwords, sessions and keeping your account safe.",
    section: "Getting started",
    body: [
      { t: "ul", items: ["Change your password from your account page at any time.", "Forgotten passwords can be reset from the sign-in page: a one-time link, valid for 30 minutes, is emailed to the account's address. Using it signs out every other session.", "Passwords are stored as salted hashes and never shown to anyone, including administrators.", "Repeated sign-in attempts are rate-limited.", "Sign out on shared computers."] },
      { t: "p", text: "If you think someone else has used your account, tell your administrator, who can deactivate it (ending every session immediately) and reset the password." },
    ],
  },
];

export const docBySlug = (slug: string) => DOCS.find((d) => d.slug === slug);
