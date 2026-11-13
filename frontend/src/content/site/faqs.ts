/** Frequently asked questions. Answers describe the product as it is today. */
export interface Faq {
  q: string;
  a: string;
  category: "Product" | "Data & security" | "Getting started" | "AI";
}

export const FAQS: Faq[] = [
  {
    category: "Product",
    q: "What is BunonBrain?",
    a: "BunonBrain is an operations system for ready-made garment factories in Bangladesh. It brings production planning, line monitoring, quality inspection, merchandising documents, approvals and workforce records into one application, with access controlled by role, factory and production line.",
  },
  {
    category: "Product",
    q: "Which parts of the product are available today?",
    a: "Production planning, the line board and floor alerts, quality inspection, merchandising, approvals, Ask BunonBrain, agents, reports, role-based access control, worker profiles and the sensor ingest API are available. The HR, roll-call and wellbeing screens run as a browser-only demonstration, and server-side attendance, shift, leave and overtime management is planned. Each product page shows its status.",
  },
  {
    category: "Getting started",
    q: "Do we need new hardware to start?",
    a: "No. You can start with the records you already keep: production output can be entered or imported from files, and order documents and inspection photos are uploaded. Machine, energy and RFID readings can be added later through the ingest API.",
  },
  {
    category: "Getting started",
    q: "Can we try it without connecting our factory?",
    a: "Yes. BunonBrain includes a simulator that produces a realistic stream of line and machine readings, so the system can be evaluated before any data or hardware is connected. Request a demo and we'll walk you through it.",
  },
  {
    category: "AI",
    q: "Does BunonBrain make decisions on its own?",
    a: "No. Agents and vision models produce recommendations. Anything that changes a plan or an assignment goes to the approval queue, where an authorised person approves or rejects it, and the decision is written to an append-only audit log.",
  },
  {
    category: "AI",
    q: "Which AI providers does it use?",
    a: "An administrator chooses the provider for Ask and agents: OpenAI, Anthropic or Google Gemini. API keys are stored encrypted in the database. Without a key, Ask and agents run in a clearly labelled demo mode, and quality inspections are stored for manual review with nothing predicted.",
  },
  {
    category: "Data & security",
    q: "Who can see our data?",
    a: "Every account has one or more of eight roles, and is scoped to factories and, for supervisors, production lines. The API checks the required permission on every request, so people only see and change what their role allows.",
  },
  {
    category: "Data & security",
    q: "How is sign-in protected?",
    a: "Passwords are stored as salted hashes. Signing in issues a short-lived access token held in memory and an HttpOnly refresh cookie. Sign-in attempts are rate-limited, and deactivating a user ends their sessions immediately.",
  },
  {
    category: "Data & security",
    q: "Where is our data stored?",
    a: "Application data is stored in a PostgreSQL database chosen when BunonBrain is deployed. Uploaded inspection images and order documents are stored in the file storage configured for the deployment. Hosting location and arrangements are agreed for each customer.",
  },
  {
    category: "Product",
    q: "Does it work in Bangla?",
    a: "Yes. The application interface is available in English and বাংলা, and users can switch between them.",
  },
];
