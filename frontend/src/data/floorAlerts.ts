// Floor alerts — notifications pushed to a supervisor's WhatsApp (simulated).
// Each alert is bound to either an approval or an insight it summarises.
// "read" tracks whether the floor supervisor has acknowledged it.

import { intBetween, makeRng, pick } from "./seed";

export type FloorAlertChannel = "whatsapp_sim";

export type FloorAlertSeverity = "info" | "warn" | "critical";

export type FloorAlert = {
  id: string;
  channel: FloorAlertChannel;
  approvalId?: string;
  insightId?: string;
  bodyEn: string;
  bodyBn: string;
  severity: FloorAlertSeverity;
  createdAt: string; // ISO timestamp
  read: boolean;
};

const TEMPLATES: Array<{
  severity: FloorAlertSeverity;
  bodyEn: string;
  bodyBn: string;
}> = [
  {
    severity: "warn",
    bodyEn: "Line {line} efficiency is {pct}% below target — review before the 10:00 standup.",
    bodyBn: "লাইন {line} এর দক্ষতা লক্ষ্যের চেয়ে {pct}% কম — ১০:০০ স্ট্যান্ডআপের আগে দেখুন।"
  },
  {
    severity: "critical",
    bodyEn: "Approval #{id} needs your sign-off — bearing replacement on {entity}.",
    bodyBn: "অনুমোদন #{id} আপনার স্বাক্ষরের অপেক্ষায় — {entity} এ বিয়ারিং প্রতিস্থাপন।"
  },
  {
    severity: "info",
    bodyEn: "Insight \"{title}\" was raised — open the brain feed for the full breakdown.",
    bodyBn: "\"{title}\" অন্তর্দৃষ্টি উত্থাপিত হয়েছে — বিস্তারিত জানতে ব্রেইন ফিড খুলুন।"
  }
];

const LINE_LABELS = ["3", "5", "7"];
const INSIGHT_TITLES = [
  { en: "Two machines trending toward bearing wear", bn: "দুটি মেশিন বিয়ারিং ক্ষয়ের দিকে এগোচ্ছে" },
  { en: "Line 3 slip — PO-4471 at risk", bn: "লাইন ৩ বিলম্ব — PO-৪৪৭১ ঝুঁকিতে" }
];

export function buildFloorAlerts(seed = 0xF100D, count = 6): FloorAlert[] {
  const rng = makeRng(seed);
  const now = Date.now();
  const out: FloorAlert[] = [];

  for (let i = 0; i < count; i++) {
    const tpl = pick(rng, TEMPLATES);
    let bodyEn = tpl.bodyEn;
    let bodyBn = tpl.bodyBn;

    let approvalId: string | undefined;
    let insightId: string | undefined;

    if (tpl.severity === "critical") {
      approvalId = `apr-${intBetween(rng, 1000, 9999)}`;
      bodyEn = bodyEn.replace("{id}", approvalId.slice(4)).replace("{entity}", `M-${intBetween(rng, 101, 310)}`);
      bodyBn = bodyBn.replace("{id}", approvalId.slice(4)).replace("{entity}", `M-${intBetween(rng, 101, 310)}`);
    } else if (tpl.severity === "info") {
      insightId = `ins-${intBetween(rng, 100, 999)}`;
      const title = pick(rng, INSIGHT_TITLES);
      bodyEn = bodyEn.replace("{title}", title.en);
      bodyBn = bodyBn.replace("{title}", title.bn);
    } else {
      bodyEn = bodyEn.replace("{line}", pick(rng, LINE_LABELS)).replace("{pct}", String(intBetween(rng, 8, 22)));
      bodyBn = bodyBn.replace("{line}", pick(rng, LINE_LABELS)).replace("{pct}", String(intBetween(rng, 8, 22)));
    }

    const createdAt = new Date(now - intBetween(rng, 5, 240) * 60_000).toISOString();

    out.push({
      id: `alert-${i + 1}`,
      channel: "whatsapp_sim",
      ...(approvalId ? { approvalId } : {}),
      ...(insightId ? { insightId } : {}),
      bodyEn,
      bodyBn,
      severity: tpl.severity,
      createdAt,
      read: rng() < 0.35
    });
  }

  return out;
}
