import type { Block } from "./blocks";

/**
 * Legal and policy documents.
 *
 * STATUS: every document is a DRAFT prepared from the BunonBrain codebase
 * (authentication, roles, database, file storage, AI providers, website
 * forms). None has been reviewed by a lawyer. Text marked [TBC: …] must be
 * confirmed by the company or its legal advisers before publication. Do not
 * add compliance claims (GDPR, ISO 27001, SOC 2, Bangladesh Data Protection
 * Act, etc.) unless they have been verified.
 *
 * To update: edit the blocks below, then change `updated`. When a document
 * is approved, set `status` to "approved".
 */

export interface LegalDoc {
  slug: string;
  title: string;
  summary: string;
  updated: string;
  status: "draft" | "approved";
  body: Block[];
}

const ENTITY = "[TBC: legal name of the company operating BunonBrain]";
const PRIVACY_CONTACT = "[TBC: privacy contact address]";

export const LEGAL: LegalDoc[] = [
  {
    slug: "privacy-policy",
    title: "Privacy Policy",
    summary: "What personal information BunonBrain collects through this website and the application, why, and the choices you have.",
    updated: "2026-11-13",
    status: "draft",
    body: [
      { t: "h2", text: "Who we are" },
      { t: "p", text: `This policy describes how ${ENTITY} ("BunonBrain", "we") handles personal information on the BunonBrain website and in the BunonBrain application. Contact for privacy questions: ${PRIVACY_CONTACT}.` },
      { t: "h2", text: "Information we collect on this website" },
      { t: "p", text: "The website has no analytics, advertising or tracking scripts. We collect personal information only when you send a form (demo request, consultation, contact, support, problem report, feature request, feedback, partnership, careers or press enquiry):" },
      { t: "ul", items: [
        "your name, and an email address and/or phone number;",
        "optionally, your company, job title and subject;",
        "your message and any answers to the form's extra questions;",
        "the page you sent the form from, and the date and time.",
      ] },
      { t: "p", text: "We do not store your IP address with your message. Your IP address is used briefly in memory to limit how many messages one connection can send per minute." },
      { t: "h2", text: "Information processed in the application" },
      { t: "p", text: "When a factory uses BunonBrain, the factory decides what information is entered. Depending on the modules in use, this can include:" },
      { t: "ul", items: [
        "**Account data:** name, email, phone, roles, factory and line assignments, last sign-in time; passwords are stored only as salted hashes.",
        "**Worker profiles:** identifiers, employment details and workstation assignments entered by the factory.",
        "**Operational data:** production lines, plans, output, sensor readings, alerts, approvals and their reasons.",
        "**Quality data:** garment inspection photos and findings.",
        "**Merchandising data:** buyer orders and uploaded documents such as purchase orders and tech packs.",
        "**Knowledge-base documents** and questions asked of Ask BunonBrain.",
        "**Audit records** of decisions and administrative changes, including who made them.",
      ] },
      { t: "p", text: `For this information the factory is the [TBC: controller], and BunonBrain processes it on the factory's behalf under the agreement with the factory [TBC: confirm roles and agreement].` },
      { t: "h2", text: "Why we use it" },
      { t: "ul", items: ["To reply to your enquiry and arrange demos or consultations.", "To provide, secure and support the application.", "To keep records required for accountability, such as the audit log.", "To improve the product, using feedback you send us."] },
      { t: "p", text: "We do not sell personal information and do not use it for advertising." },
      { t: "h2", text: "Who it is shared with" },
      { t: "ul", items: [
        "**Hosting and database providers** that run the website, API, database and file storage: [TBC: list of hosting providers and their locations].",
        "**Email delivery** for password-reset messages: [TBC: email provider].",
        "**AI providers**, only if the factory's administrator configures one (OpenAI, Anthropic or Google Gemini): the text of a question and the document passages used to answer it, inspection images (if vision analysis via the language model is enabled) and document text (if language-model extraction is enabled) are sent to that provider to produce a result.",
        "Authorities, where required by law.",
      ] },
      { t: "h2", text: "How long we keep it" },
      { t: "p", text: "Website enquiries are kept for [TBC: retention period]. Application data is kept for the duration of the factory's agreement and handled at its end as set out in that agreement [TBC]. Audit-log entries cannot be edited or deleted through the application. See the Data Retention Policy." },
      { t: "h2", text: "Security" },
      { t: "p", text: "See the Security Policy for how accounts, sessions and data are protected." },
      { t: "h2", text: "Your choices and rights" },
      { t: "p", text: `You can ask to access, correct or delete personal information you sent through the website by writing to ${PRIVACY_CONTACT}. For information held in a factory's BunonBrain account, please contact that factory first. Rights available to you depend on applicable law [TBC: applicable data protection law].` },
      { t: "h2", text: "Changes" },
      { t: "p", text: "We will update this page when our practices change and show the date of the latest version above." },
    ],
  },
  {
    slug: "terms-of-service",
    title: "Terms of Service",
    summary: "The terms for using this website and the BunonBrain application.",
    updated: "2026-11-13",
    status: "draft",
    body: [
      { t: "h2", text: "About these terms" },
      { t: "p", text: `These terms apply to the BunonBrain website and application provided by ${ENTITY}. Use of the application by a factory is also governed by its customer agreement, which prevails if the two conflict [TBC].` },
      { t: "h2", text: "Accounts" },
      { t: "ul", items: ["Accounts are created by the factory's administrators, who assign roles and scope.", "Keep your password confidential and tell your administrator if you suspect misuse.", "Administrators are responsible for giving each person only the access they need."] },
      { t: "h2", text: "Acceptable use" },
      { t: "p", text: "You must use BunonBrain in line with the Acceptable Use Policy." },
      { t: "h2", text: "Customer data" },
      { t: "p", text: "Factories keep their rights in the data they enter. BunonBrain uses customer data only to provide and support the service, as described in the Privacy Policy and the customer agreement." },
      { t: "h2", text: "AI-generated output" },
      { t: "p", text: "Answers, recommendations and detections produced by AI features may be incomplete or wrong. They are suggestions: decisions that change plans, assignments or quality outcomes are made by authorised people, and you are responsible for checking output before relying on it." },
      { t: "h2", text: "Demo and planned features" },
      { t: "p", text: "Features labelled demo or planned are not part of the delivered service unless the customer agreement says otherwise." },
      { t: "h2", text: "Availability" },
      { t: "p", text: "We aim to keep the service available but do not guarantee uninterrupted operation unless a service level is agreed in writing [TBC]." },
      { t: "h2", text: "Liability" },
      { t: "p", text: "[TBC: limitation of liability, warranties and indemnities, to be drafted by legal counsel.]" },
      { t: "h2", text: "Governing law" },
      { t: "p", text: "These terms are governed by [TBC: governing law and jurisdiction]." },
      { t: "h2", text: "Changes" },
      { t: "p", text: "We may update these terms. Material changes will be communicated to customers in advance [TBC: notice period]." },
    ],
  },
  {
    slug: "cookie-policy",
    title: "Cookie Policy",
    summary: "The cookies and browser storage BunonBrain uses, and why. No advertising or tracking cookies.",
    updated: "2026-11-13",
    status: "draft",
    body: [
      { t: "h2", text: "Summary" },
      { t: "p", text: "The public website sets no cookies and uses no analytics, advertising or social-media trackers. The application uses one strictly necessary cookie for sign-in, and browser storage for your preferences." },
      { t: "h2", text: "Cookies" },
      { t: "table", head: ["Name", "Set by", "Purpose", "Lifetime"], rows: [
        ["fb_refresh", "Application, when you sign in", "Keeps you signed in by renewing the short-lived access token. HttpOnly, so scripts in the page cannot read it.", "Up to 7 days, or until you sign out"],
      ] },
      { t: "p", text: "This cookie is strictly necessary for signing in and cannot be switched off while using the application." },
      { t: "h2", text: "Browser storage" },
      { t: "p", text: "The application stores preferences in your browser's local storage, for example theme, language, display density, recent searches, whether you completed the guided tour and onboarding answers. The Workforce demo also keeps its sample data there. This information stays on your device and is not sent to our servers." },
      { t: "p", text: "You can clear it at any time through your browser's settings; the application will return to its defaults." },
      { t: "h2", text: "Fonts" },
      { t: "p", text: "Fonts are served from our own domain; your browser does not contact a third-party font service when you visit." },
      { t: "h2", text: "Changes" },
      { t: "p", text: "If we add cookies, we will list them here before they are used." },
    ],
  },
  {
    slug: "data-protection-policy",
    title: "Data Protection Policy",
    summary: "The principles and controls BunonBrain applies to personal and factory data.",
    updated: "2026-11-13",
    status: "draft",
    body: [
      { t: "note", tone: "draft", text: "This policy describes controls built into the BunonBrain application. It is not a statement of compliance with any specific law or standard." },
      { t: "h2", text: "Principles" },
      { t: "ul", items: ["**Minimisation:** forms and screens ask only for what the task needs.", "**Purpose:** data is used only to provide and support the service.", "**Least privilege:** access is granted by role, factory and production line.", "**Accountability:** decisions and administrative changes are recorded.", "**Transparency:** simulated, demo and AI-generated content is labelled."] },
      { t: "h2", text: "Access control" },
      { t: "ul", items: ["Eight system roles grant permissions; the API checks a permission on every request.", "Accounts are scoped to factories; Production Supervisors also to lines.", "Employment details in worker profiles are visible only to roles with the manage-workers permission.", "Deactivating an account ends all of its sessions immediately."] },
      { t: "h2", text: "Separation between factories" },
      { t: "p", text: "Factories are separate tenants. People see data only from the factories they are assigned to; only Super Admins have platform-wide access." },
      { t: "h2", text: "Data sent to third parties" },
      { t: "p", text: "AI providers receive data only when an administrator configures them, and only the data needed for the request (see the Privacy Policy). API keys for these providers are stored encrypted." },
      { t: "h2", text: "Incidents" },
      { t: "p", text: "Suspected security or data incidents should be reported through the problem-report form with the type set to Security. [TBC: incident response and notification procedure and timelines.]" },
      { t: "h2", text: "Responsibilities" },
      { t: "p", text: "[TBC: name or role of the person responsible for data protection.]" },
    ],
  },
  {
    slug: "acceptable-use-policy",
    title: "Acceptable Use Policy",
    summary: "What you may and may not do when using BunonBrain.",
    updated: "2026-11-13",
    status: "draft",
    body: [
      { t: "h2", text: "You may" },
      { t: "ul", items: ["Use BunonBrain for your factory's legitimate operations, within the access your role grants.", "Upload documents, images and data your organisation is entitled to use."] },
      { t: "h2", text: "You must not" },
      { t: "ul", items: [
        "Share your account, or use someone else's.",
        "Try to access factories, lines or records outside your assigned scope, or bypass permission checks.",
        "Upload unlawful content, malware, or personal information you are not entitled to process.",
        "Probe, scan or load-test the service without written permission.",
        "Send automated or bulk submissions through the website forms.",
        "Use AI features to make decisions about individuals without human review.",
        "Use BunonBrain to monitor workers in ways not permitted by law or by your organisation's policies.",
      ] },
      { t: "h2", text: "Enforcement" },
      { t: "p", text: "Misuse may lead to suspension of access. Administrators can deactivate accounts at any time." },
      { t: "h2", text: "Reporting misuse" },
      { t: "p", text: "Report suspected misuse or vulnerabilities through the problem-report form, choosing Security." },
    ],
  },
  {
    slug: "security-policy",
    title: "Security Policy",
    summary: "How BunonBrain protects accounts, sessions and data, and how to report a vulnerability.",
    updated: "2026-11-13",
    status: "draft",
    body: [
      { t: "note", tone: "draft", text: "This page describes security controls implemented in the BunonBrain application. BunonBrain does not currently hold an independent security certification." },
      { t: "h2", text: "Accounts and sign-in" },
      { t: "ul", items: [
        "Passwords are stored as salted hashes; nobody, including administrators, can read them.",
        "Signing in issues a short-lived access token (15 minutes) held in memory, and an HttpOnly refresh cookie valid for up to 7 days.",
        "Sign-in, registration and password-reset requests are rate-limited per IP address.",
        "Password-reset links work once and expire after 30 minutes; using one signs out other sessions.",
        "Deactivating an account ends all of its sessions immediately.",
      ] },
      { t: "h2", text: "Authorisation" },
      { t: "ul", items: ["Every API endpoint declares the permission it requires; requests without it are refused.", "Data is scoped by factory and, for supervisors, by production line.", "High-risk approvals require a separate permission."] },
      { t: "h2", text: "Audit" },
      { t: "p", text: "Approval decisions and administrative changes are written to an audit log in the same database transaction as the change. The database rejects updates and deletions of audit entries." },
      { t: "h2", text: "Data handling" },
      { t: "ul", items: ["API keys for AI providers are stored encrypted.", "Uploaded images are checked to match their declared type.", "Uploaded files are stored under server-generated names, not the names supplied by users.", "Website forms are rate-limited and store no IP address."] },
      { t: "h2", text: "Infrastructure" },
      { t: "p", text: "[TBC: hosting provider, region, encryption at rest and in transit, backups and their testing, and access to production systems.]" },
      { t: "h2", text: "Reporting a vulnerability" },
      { t: "p", text: "Please report vulnerabilities through the problem-report form with the type set to Security, and give us reasonable time to fix the issue before disclosing it. Do not access other people's data or disrupt the service while testing." },
    ],
  },
  {
    slug: "data-retention-policy",
    title: "Data Retention Policy",
    summary: "How long different kinds of data are kept, and how they are removed.",
    updated: "2026-11-13",
    status: "draft",
    body: [
      { t: "note", tone: "draft", text: "Retention periods below have not yet been decided. They must be set by the company, in agreement with customers and in line with applicable law." },
      { t: "table", head: ["Data", "Where", "Retention"], rows: [
        ["Website enquiries", "Inquiries table", "[TBC: e.g. period after the enquiry is closed]"],
        ["User accounts", "Application database", "While the factory's agreement is active; deactivated accounts [TBC]"],
        ["Worker profiles", "Application database", "Set by the factory [TBC]"],
        ["Production, quality and merchandising records", "Application database", "For the duration of the agreement [TBC]"],
        ["Inspection images and order documents", "Configured file storage", "For the duration of the agreement [TBC]"],
        ["Audit log", "Application database (append-only)", "Not editable or deletable through the application; period [TBC]"],
        ["Password-reset tokens", "Application database", "Valid for 30 minutes; usable once"],
        ["Sign-in refresh cookie", "Your browser", "Up to 7 days, or until sign-out"],
        ["Preferences in browser storage", "Your browser", "Until you clear them"],
      ] },
      { t: "h2", text: "End of agreement" },
      { t: "p", text: "[TBC: export of customer data and deletion timelines when an agreement ends, including backups.]" },
      { t: "h2", text: "Requests" },
      { t: "p", text: "Requests to delete website enquiries can be sent through the contact form." },
    ],
  },
  {
    slug: "disclaimer",
    title: "Disclaimer",
    summary: "Limits on the information published on this website.",
    updated: "2026-11-13",
    status: "draft",
    body: [
      { t: "h2", text: "Website content" },
      { t: "p", text: "Information on this website is provided for general information about BunonBrain. It is not professional, legal or engineering advice, and we do not guarantee that it is complete or current." },
      { t: "h2", text: "Product screenshots and examples" },
      { t: "p", text: "Screenshots show the BunonBrain application running on simulated or sample data. Figures in examples are illustrative and are not readings from a real factory." },
      { t: "h2", text: "Planned features" },
      { t: "p", text: "Features labelled planned are under development. Descriptions of them are not commitments to deliver them, or to deliver them by a particular date." },
      { t: "h2", text: "Articles" },
      { t: "p", text: "Articles explain standard industry measures and practices. Apply them with judgement to your own operations." },
      { t: "h2", text: "External links" },
      { t: "p", text: "We are not responsible for the content of other websites linked from this site." },
    ],
  },
  {
    slug: "intellectual-property-policy",
    title: "Intellectual Property Policy",
    summary: "Ownership of the BunonBrain software, brand and content, and how to report infringement.",
    updated: "2026-11-13",
    status: "draft",
    body: [
      { t: "h2", text: "Our intellectual property" },
      { t: "p", text: `The BunonBrain software, the BunonBrain name, logo and brand assets, and the content of this website are owned by ${ENTITY} or its licensors [TBC: confirm ownership and any registered trade marks].` },
      { t: "h2", text: "Brand use" },
      { t: "p", text: "Media and partners may use the logo files from our brand page to refer to BunonBrain, following the usage guidance there. Other uses need our written permission." },
      { t: "h2", text: "Third-party components" },
      { t: "p", text: "BunonBrain uses open-source software under its respective licences. The wordmark is drawn from the Outfit typeface, licensed under the SIL Open Font License." },
      { t: "h2", text: "Customer content" },
      { t: "p", text: "Factories keep ownership of the data, documents and images they upload." },
      { t: "h2", text: "Reporting infringement" },
      { t: "p", text: "If you believe content on this website infringes your rights, contact us through the contact form with details of the work and where it appears." },
    ],
  },
  {
    slug: "accessibility-statement",
    title: "Accessibility Statement",
    summary: "Our commitment to an accessible website and application, and how to report a barrier.",
    updated: "2026-11-13",
    status: "draft",
    body: [
      { t: "h2", text: "Our approach" },
      { t: "p", text: "We want BunonBrain to be usable by everyone, including people who use keyboards, screen readers or magnification. We design towards the Web Content Accessibility Guidelines (WCAG) 2.1 at level AA. This website has not yet been independently audited, so we do not claim full conformance." },
      { t: "h2", text: "What we do" },
      { t: "ul", items: [
        "Semantic headings, landmarks and a skip-to-content link on every page.",
        "Keyboard-operable menus and forms, with visible focus.",
        "Text and controls designed for at least 4.5:1 contrast.",
        "Form errors announced and linked to their fields.",
        "Descriptive alternative text for product screenshots and diagrams.",
        "Reduced motion respected where animations are used.",
        "The application interface in English and বাংলা.",
      ] },
      { t: "h2", text: "Known limitations" },
      { t: "ul", items: ["Some charts in the application may not yet have full text alternatives."] },
      { t: "h2", text: "Feedback" },
      { t: "p", text: "If you find a barrier, please tell us through the feedback form and describe the page and what happened. We will look into it." },
    ],
  },
];

export const legalBySlug = (slug: string) => LEGAL.find((l) => l.slug === slug);
