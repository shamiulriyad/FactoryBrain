// Insight factory — produces the seeded insights that the demo surfaces.
// Each insight is derived from seed and carries explicit evidence refs.
import type { HealthSnapshot } from "./analytics";
import type { Order } from "./orders";
import type { InventoryRecord } from "./inventory";
import type { Customer } from "./customers";
import type { Product } from "./products";

export type InsightStage = "suggested" | "pending_approval" | "executing" | "done" | "logged" | "rejected" | "failed";

export type RiskTier = "low" | "medium" | "high";

export type EvidenceRef = {
  domain: "orders" | "customers" | "products" | "inventory" | "conversations" | "policies" | "suppliers" | "manuals";
  count: number;
  filter?: Record<string, string | number | boolean>;
  previewIds?: string[];
};

export type Insight = {
  id: string;
  agentId: string;
  agentLabel: string;
  title: string;
  titleBn: string;
  finding: string;
  findingBn: string;
  factors: Array<{ label: string; labelBn: string; magnitude: string; magnitudeBn: string }>;
  recommendation: {
    title: string;
    titleBn: string;
    action: string;
    actionBn: string;
    riskTier: RiskTier;
    targetStage: InsightStage;
  };
  evidence: EvidenceRef[];
  stage: InsightStage;
  createdAt: string;
  updatedAt: string;
  confidence: number; // 0..1
  pinned?: boolean;
};

const now = new Date();
const isoAgo = (ms: number) => new Date(now.getTime() - ms).toISOString();

export function buildInsights(
  health: HealthSnapshot,
  inventory: InventoryRecord[],
  products: Product[],
  customers: Customer[],
  orders: Order[]
): Insight[] {
  const out: Insight[] = [];

  // 1) Line 3 efficiency below target — Line Throughput (Medium)
  {
    out.push({
      id: "ins-1",
      agentId: "line-throughput-agent",
      agentLabel: "Line Efficiency",
      title: "Line 3 efficiency trending 14% below target",
      titleBn: "লাইন ৩ এর দক্ষতা লক্ষ্যের চেয়ে ১৪% কম",
      finding:
        "Bundles scanned over the last 2 hours on Line 3 imply a line efficiency of 84%, against the daily target of 98%. The bottleneck operation is Sewing (SMV 0.55), where average bundle cycle time exceeds the SAH-based allowance.",
      findingBn:
        "গত ২ ঘন্টায় লাইন ৩ এ স্ক্যান করা বান্ডলগুলো ৮৪% দক্ষতা নির্দেশ করে, দৈনিক লক্ষ্য ৯৮% এর বিপরীতে। বটলনেক অপারেশন হলো সেলাই (SMV ০.৫৫), যেখানে গড় বান্ডল সাইকেল টাইম SAH-ভিত্তিক অনুমোদিত সময়ের চেয়ে বেশি।",
      factors: [
        { label: "Line 3 efficiency", labelBn: "লাইন ৩ দক্ষতা", magnitude: "84%", magnitudeBn: "৮৪%" },
        { label: "Target", labelBn: "লক্ষ্য", magnitude: "98%", magnitudeBn: "৯৮%" },
        { label: "Bottleneck op", labelBn: "বটলনেক অপারেশন", magnitude: "Sewing", magnitudeBn: "সেলাই" }
      ],
      recommendation: {
        title: "Re-balance Line 3 towards Sewing",
        titleBn: "লাইন ৩ কে সেলাইমুখী করে পুনর্ভারসাম্য করুন",
        action: "Move two helpers from Finishing to Sewing for the next 90 minutes; reassess at shift handover.",
        actionBn: "পরবর্তী ৯০ মিনিটের জন্য ফিনিশিং থেকে সেলাইতে দুজন সহায়ক সরান; শিফট হ্যান্ডওভারে পুনর্মূল্যায়ন।",
        riskTier: "medium",
        targetStage: "pending_approval"
      },
      evidence: [
        { domain: "orders", count: 84, filter: { lineId: "line-3", window: "2h" }, previewIds: ["po-4471", "po-4502"] },
        { domain: "inventory", count: 36 }
      ],
      stage: "pending_approval",
      createdAt: isoAgo(45 * 60 * 1000),
      updatedAt: isoAgo(45 * 60 * 1000),
      confidence: 0.86,
      pinned: true
    });
  }

  // 2) Maintenance: two machines trending to failure (Medium)
  {
    out.push({
      id: "ins-2",
      agentId: "maintenance-agent",
      agentLabel: "Maintenance & Uptime",
      title: "Two sewing machines trending toward bearing wear",
      titleBn: "দুটি সেলাই মেশিন বিয়ারিং ক্ষয়ের দিকে এগোচ্ছে",
      finding:
        "Vibration RMS and motor temperature on M-12 (Line 2) and M-27 (Line 4) have crossed the warning band over the last 90 minutes — shapes match NASA C-MAPSS-style degradation. Forecast: probability of unplanned stop within 7 days is 38% and 31% respectively.",
      findingBn:
        "গত ৯০ মিনিটে M-১২ (লাইন ২) এবং M-২৭ (লাইন ৪) এ কম্পন RMS এবং মোটর তাপমাত্রা সতর্কতা ব্যান্ড অতিক্রম করেছে — NASA C-MAPSS-স্টাইল ক্ষয়ের সাথে মিলে। পূর্বাভাস: ৭ দিনের মধ্যে অপরিকল্পিত বন্ধের সম্ভাবনা যথাক্রমে ৩৮% এবং ৩১%।",
      factors: [
        { label: "M-12 vibration RMS", labelBn: "M-১২ কম্পন RMS", magnitude: "+2.4σ", magnitudeBn: "+২.৪σ" },
        { label: "M-27 motor temp", labelBn: "M-২৭ মোটর তাপমাত্রা", magnitude: "+3.1σ", magnitudeBn: "+৩.১σ" },
        { label: "Line 2 PoF (7d)", labelBn: "লাইন ২ PoF (৭ দিন)", magnitude: "38%", magnitudeBn: "৩৮%" }
      ],
      recommendation: {
        title: "Schedule bearing inspection for M-12 and M-27",
        titleBn: "M-১২ এবং M-২৭ এর জন্য বিয়ারিং পরীক্ষা শিডিউল করুন",
        action: "Open a maintenance work order; defer non-critical production on those machines for ~30 min.",
        actionBn: "মেইনটেন্যান্স ওয়ার্ক অর্ডার খুলুন; ঐ মেশিনগুলোতে অ-জরুরি উৎপাদন ~৩০ মিনিটের জন্য স্থগিত করুন।",
        riskTier: "medium",
        targetStage: "pending_approval"
      },
      evidence: [
        { domain: "inventory", count: 36, filter: { machinesAtRisk: 2 }, previewIds: ["machine-12", "machine-27"] },
        { domain: "policies", count: 1, previewIds: ["policy:maintenance-sop-1"] }
      ],
      stage: "suggested",
      createdAt: isoAgo(2 * 60 * 60 * 1000),
      updatedAt: isoAgo(2 * 60 * 60 * 1000),
      confidence: 0.79,
      pinned: true
    });
  }

  // 3) Orchestrator morning brief — shipment risk (Medium)
  {
    out.push({
      id: "ins-3",
      agentId: "manager-agent",
      agentLabel: "Manager Orchestrator",
      title: "Shipment risk for PO-4471 due to Line 3 slip",
      titleBn: "লাইন ৩ এর বিলম্বের কারণে PO-৪৪৭১ এ শিপমেন্ট ঝুঁকি",
      finding:
        "Line 3 efficiency is below target and PO-4471 (H&M buyer, 5,200 polo units) ships in 36 hours. Orchestrator sub-routes to the line-efficiency agent: at current pace the order will miss its shipment window by ~14 hours. Recommend re-routing 1,800 units to Line 1.",
      findingBn:
        "লাইন ৩ এর দক্ষতা লক্ষ্যের নিচে এবং PO-৪৪৭১ (H&M ক্রেতা, ৫,২০০ পোলো ইউনিট) ৩৬ ঘন্টায় শিপ হবে। অর্কেস্ট্রেটর লাইন-দক্ষতা এজেন্টে সাব-রাউট করে: বর্তমান গতিতে অর্ডারটি শিপমেন্ট উইন্ডো ~১৪ ঘন্টা মিস করবে। ১,৮০০ ইউনিট লাইন ১ এ পুনর্নির্দেশ করার সুপারিশ।",
      factors: [
        { label: "Order value", labelBn: "অর্ডার মূল্য", magnitude: "৳6.8L", magnitudeBn: "৳৬.৮ লক্ষ" },
        { label: "Hours behind", labelBn: "পিছিয়ে ঘন্টা", magnitude: "14h", magnitudeBn: "১৪ ঘন্টা" },
        { label: "Line 1 spare capacity", labelBn: "লাইন ১ অতিরিক্ত ক্ষমতা", magnitude: "32%", magnitudeBn: "৩২%" }
      ],
      recommendation: {
        title: "Re-route 1,800 units to Line 1",
        titleBn: "১,৮০০ ইউনিট লাইন ১ এ পুনর্নির্দেশ",
        action: "Split the PO at the next operation handover; flag buyer for proactive delay notice.",
        actionBn: "পরবর্তী অপারেশন হ্যান্ডওভারে PO ভাগ করুন; সক্রিয় বিলম্ব বিজ্ঞপ্তির জন্য ক্রেতাকে ফ্ল্যাগ করুন।",
        riskTier: "medium",
        targetStage: "pending_approval"
      },
      evidence: [
        { domain: "orders", count: 1, filter: { poId: "po-4471" }, previewIds: ["po-4471"] },
        { domain: "customers", count: 1, previewIds: ["buyer-hm"] }
      ],
      stage: "suggested",
      createdAt: isoAgo(20 * 60 * 1000),
      updatedAt: isoAgo(20 * 60 * 1000),
      confidence: 0.74
    });
  }

  // 4) Orchestrator: buyer complaint cluster — QC pass-rate (Low)
  {
    out.push({
      id: "ins-4",
      agentId: "manager-agent",
      agentLabel: "Manager Orchestrator",
      title: "AQL pass-rate dropping on the C&A order",
      titleBn: "C&A অর্ডারে AQL পাস-হার কমছে",
      finding:
        "Final QC AQL pass-rate on PO-4493 (C&A, denim) fell from 96% to 88% across the last four inspections. Orchestrator routes to compliance docs: Higg FEM and the buyer code both flag stitch density as the most common defect reason.",
      findingBn:
        "PO-৪৪৯৩ (C&A, ডেনিম) এর চূড়ান্ত QC AQL পাস-হার শেষ চারটি পরিদর্শনে ৯৬% থেকে ৮৮% এ নেমেছে। অর্কেস্ট্রেটর সম্মতি নথিতে রাউট করে: Higg FEM এবং ক্রেতা কোড উভয়ই সেলাই ঘনত্বকে সবচেয়ে সাধারণ ত্রুটি কারণ হিসেবে চিহ্নিত করে।",
      factors: [
        { label: "AQL pass-rate Δ", labelBn: "AQL পাস-হার পরিবর্তন", magnitude: "−8pp", magnitudeBn: "−৮ শতাংশ পয়েন্ট" },
        { label: "Inspections reviewed", labelBn: "পর্যালোচিত পরিদর্শন", magnitude: "4", magnitudeBn: "৪" }
      ],
      recommendation: {
        title: "Pause Line 4 stitching, retrain operators on stitch density",
        titleBn: "লাইন ৪ সেলাই স্থগিত, সেলাই ঘনত্বে অপারেটরদের পুনরায় প্রশিক্ষণ",
        action: "Open a 30-minute coaching window on Line 4 stitching.",
        actionBn: "লাইন ৪ সেলাইতে ৩০ মিনিটের কোচিং উইন্ডো খুলুন।",
        riskTier: "low",
        targetStage: "suggested"
      },
      evidence: [
        { domain: "policies", count: 2, previewIds: ["policy:higg-fem-1", "policy:ca-code-1"] },
        { domain: "orders", count: 1, filter: { poId: "po-4493" } }
      ],
      stage: "suggested",
      createdAt: isoAgo(6 * 60 * 60 * 1000),
      updatedAt: isoAgo(6 * 60 * 60 * 1000),
      confidence: 0.72
    });
  }

  // 5) Line Throughput: throughput target met (Low, done)
  {
    out.push({
      id: "ins-5",
      agentId: "line-throughput-agent",
      agentLabel: "Line Efficiency",
      title: "Line 1 hit 103% of its daily target",
      titleBn: "লাইন ১ দৈনিক লক্ষ্যের ১০৩% অর্জন করেছে",
      finding:
        "Bundles scanned across the shift on Line 1 came in at 103% of the planned units for the H&M polo PO. SAH consumption is 7% under budget; the surplus opens capacity for the redirected PO-4471 units.",
      findingBn:
        "লাইন ১ এ শিফট জুড়ে স্ক্যান করা বান্ডলগুলো H&M পোলো PO এর জন্য পরিকল্পিত ইউনিটের ১০৩% এ এসেছে। SAH খরচ বাজেটের চেয়ে ৭% কম; উদ্বৃত্ত ক্ষমতা পুনর্নির্দেশিত PO-৪৪৭১ ইউনিটের জন্য উন্মুক্ত।",
      factors: [
        { label: "Actual vs target", labelBn: "বাস্তব বনাম লক্ষ্য", magnitude: "103%", magnitudeBn: "১০৩%" },
        { label: "SAH variance", labelBn: "SAH ভ্যারিয়েন্স", magnitude: "−7%", magnitudeBn: "−৭%" }
      ],
      recommendation: {
        title: "Acknowledge and preserve Line 1 capacity",
        titleBn: "স্বীকার করুন এবং লাইন ১ ক্ষমতা সংরক্ষণ",
        action: "Hold a 90-minute reserve window for redirected Line 3 units.",
        actionBn: "পুনর্নির্দেশিত লাইন ৩ ইউনিটের জন্য ৯০ মিনিটের রিজার্ভ উইন্ডো রাখুন।",
        riskTier: "low",
        targetStage: "logged"
      },
      evidence: [
        { domain: "orders", count: 1, previewIds: ["po-4471"] },
        { domain: "inventory", count: 12 }
      ],
      stage: "done",
      createdAt: isoAgo(22 * 60 * 60 * 1000),
      updatedAt: isoAgo(22 * 60 * 60 * 1000),
      confidence: 0.95
    });
  }

  // 6) Maintenance: routine inspection logged (Logged)
  {
    out.push({
      id: "ins-6",
      agentId: "maintenance-agent",
      agentLabel: "Maintenance & Uptime",
      title: "Weekly bearing sweep completed on Line 2",
      titleBn: "লাইন ২ এ সাপ্তাহিক বিয়ারিং সুইপ সম্পন্ন",
      finding:
        "Routine ultrasonic inspection ran on all 12 sewing machines of Line 2. 11 machines within tolerance, 1 marginal (M-09) booked for re-inspect next week. No production stop required.",
      findingBn:
        "লাইন ২ এর সব ১২টি সেলাই মেশিনে রুটিন আল্ট্রাসনিক পরীক্ষা চালানো হয়েছে। ১১টি মেশিন সহনশীলতার মধ্যে, ১টি মার্জিনাল (M-০৯) পরের সপ্তাহে পুনঃপরীক্ষার জন্য বুক করা হয়েছে। উৎপাদন বন্ধ প্রয়োজন নেই।",
      factors: [
        { label: "Machines inspected", labelBn: "পরীক্ষিত মেশিন", magnitude: "12", magnitudeBn: "১২" },
        { label: "Marginal", labelBn: "মার্জিনাল", magnitude: "1", magnitudeBn: "১" }
      ],
      recommendation: {
        title: "No action required",
        titleBn: "কোনো পদক্ষেপ প্রয়োজন নেই",
        action: "Acknowledge.",
        actionBn: "স্বীকার করুন।",
        riskTier: "low",
        targetStage: "done"
      },
      evidence: [
        { domain: "policies", count: 1, previewIds: ["policy:maintenance-sop-1"] }
      ],
      stage: "done",
      createdAt: isoAgo(26 * 60 * 60 * 1000),
      updatedAt: isoAgo(26 * 60 * 60 * 1000),
      confidence: 0.99
    });
  }

  return out;
}

function bn(n: number) {
  const map: Record<number, string> = { 0: "০", 1: "১", 2: "২", 3: "৩", 4: "৪", 5: "৫", 6: "৬", 7: "৭", 8: "৮", 9: "৯" };
  return String(n).replace(/[0-9]/g, (d) => map[Number(d)]);
}
