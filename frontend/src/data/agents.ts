// BunonBrain — three-agent roster for a Bangladeshi RMG factory floor.
// Each agent is bound to one or more MCP-style tools under src/services/factory.tools.ts.
export type AgentDef = {
  id: string;
  name: string;
  nameBn: string;
  purpose: string;
  purposeBn: string;
  risk: "low" | "medium" | "high";
  execution: "auto" | "approval_required" | "auto_suggest_with_threshold" | "per_policy";
  model: string;
  contextSlices: string[];
  status: "ready" | "draft";
};

export const agents: AgentDef[] = [
  {
    id: "line-throughput-agent",
    name: "Line Efficiency Agent",
    nameBn: "লাইন দক্ষতা এজেন্ট",
    purpose:
      "Reads line and order data, computes efficiency against target, and names the bottleneck operation dragging the line down.",
    purposeBn:
      "লাইন ও অর্ডার ডেটা পড়ে লক্ষ্যমাত্রার বিপরীতে দক্ষতা গণনা করে এবং লাইনকে পিছিয়ে রাখা বটলনেক অপারেশন চিহ্নিত করে।",
    risk: "low",
    execution: "auto",
    model: "Mid-tier reasoning (GPT-4.1-mini / Claude Sonnet small)",
    contextSlices: ["lines", "orders", "targets"],
    status: "ready"
  },
  {
    id: "maintenance-agent",
    name: "Maintenance & Uptime Agent",
    nameBn: "মেইনটেন্যান্স ও আপটাইম এজেন্ট",
    purpose:
      "Reads machine sensor data (vibration, temperature, duty cycle) and flags machines trending toward failure before they go down.",
    purposeBn:
      "মেশিনের সেন্সর ডেটা (কম্পন, তাপমাত্রা, ডিউটি সাইকেল) পড়ে ব্যর্থতার দিকে এগোচ্ছে এমন মেশিন আগেই চিহ্নিত করে।",
    risk: "medium",
    execution: "auto_suggest_with_threshold",
    model: "Mid-tier reasoning + structured threshold checks (GPT-4.1-mini)",
    contextSlices: ["machines", "lines"],
    status: "ready"
  },
  {
    id: "manager-agent",
    name: "Manager Orchestrator Agent",
    nameBn: "ম্যানেজার অর্কেস্ট্রেটর এজেন্ট",
    purpose:
      "Routes free-text factory-floor questions to the right sub-agent and generates the daily brief (overnight efficiency, downtime, today's shipment risk) on demand.",
    purposeBn:
      "মুক্ত-পাঠ্য কারখানা-তল প্রশ্নগুলো সঠিক সাব-এজেন্টে রাউট করে এবং চাহিদা অনুযায়ী দৈনিক ব্রিফ (রাতের দক্ষতা, ডাউনটাইম, আজকের শিপমেন্ট ঝুঁকি) তৈরি করে।",
    risk: "low",
    execution: "auto",
    model: "Frontier-tier routing + summarisation (Claude Sonnet / GPT-4.1)",
    contextSlices: ["*"],
    status: "ready"
  }
];
