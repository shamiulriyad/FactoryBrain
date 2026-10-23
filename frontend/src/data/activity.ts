// Seed activity log: built from the insight lifecycle.
// Real prototype: every state transition writes here.

export type ActivityItem = {
  id: string;
  actor: "user" | string; // user or agentId
  actorLabel: string;
  verb: string;
  verbBn: string;
  target?: string;
  targetBn?: string;
  outcome?: "approved" | "rejected" | "executed" | "completed" | "failed" | "flagged";
  isoDate: string;
};

const now = Date.now();
const ago = (ms: number) => new Date(now - ms).toISOString();

export function buildActivity(): ActivityItem[] {
  return [
    {
      id: "act-1",
      actor: "maintenance-agent",
      actorLabel: "Maintenance & Uptime",
      verb: "completed weekly bearing sweep",
      verbBn: "সাপ্তাহিক বিয়ারিং সুইপ সম্পন্ন করেছে",
      target: "Line 2 · 12 machines",
      targetBn: "লাইন ২ · ১২টি মেশিন",
      outcome: "completed",
      isoDate: ago(2 * 60 * 60 * 1000)
    },
    {
      id: "act-2",
      actor: "maintenance-agent",
      actorLabel: "Maintenance & Uptime",
      verb: "raised insight",
      verbBn: "অন্তর্দৃষ্টি উত্থাপন করেছে",
      target: "Two machines trending toward bearing wear",
      targetBn: "দুটি মেশিন বিয়ারিং ক্ষয়ের দিকে এগোচ্ছে",
      outcome: "flagged",
      isoDate: ago(3 * 60 * 60 * 1000)
    },
    {
      id: "act-3",
      actor: "user",
      actorLabel: "You",
      verb: "connected",
      verbBn: "সংযুক্ত করেছেন",
      target: "RFID bundle scans (simulated)",
      targetBn: "RFID বান্ডেল স্ক্যান (সিমুলেটেড)",
      outcome: "completed",
      isoDate: ago(5 * 60 * 60 * 1000)
    },
    {
      id: "act-4",
      actor: "manager-agent",
      actorLabel: "Manager Orchestrator",
      verb: "drafted morning brief",
      verbBn: "সকালের ব্রিফ খসড়া তৈরি করেছে",
      target: "Line 3 slip — PO-4471 at risk",
      targetBn: "লাইন ৩ বিলম্ব — PO-৪৪৭১ ঝুঁকিতে",
      outcome: "flagged",
      isoDate: ago(6 * 60 * 60 * 1000)
    },
    {
      id: "act-5",
      actor: "manager-agent",
      actorLabel: "Manager Orchestrator",
      verb: "routed question",
      verbBn: "প্রশ্ন রাউট করেছে",
      target: "“Which machines need maintenance this week?”",
      targetBn: "“এই সপ্তাহে কোন মেশিনে রক্ষণাবেক্ষণ দরকার?”",
      outcome: "completed",
      isoDate: ago(20 * 60 * 60 * 1000)
    },
    {
      id: "act-6",
      actor: "line-throughput-agent",
      actorLabel: "Line Efficiency",
      verb: "explained anomaly",
      verbBn: "অসঙ্গতি ব্যাখ্যা করেছে",
      target: "Line 3 efficiency 14% below target",
      targetBn: "লাইন ৩ এর দক্ষতা লক্ষ্যের চেয়ে ১৪% কম",
      outcome: "completed",
      isoDate: ago(24 * 60 * 60 * 1000)
    }
  ];
}
