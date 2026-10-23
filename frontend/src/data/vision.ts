// Vision results — and a re-export of the manual knowledge corpus from
// `src/data/manuals.ts`. The corpus is small, curated, and powers the Ask
// page + Manager agent tool path. No external vector DB.
//
// Seed producers follow the project's established pattern (see
// `buildActivity`, `buildInsights`, `buildSensorReadings`):
//   - deterministic seed via `makeRng`
//   - both English + Bangla fields populated
//   - `machineId` / `source` bound to existing store ids so no foreign keys
//     are introduced
// Both are Simulated in the prototype.

import { intBetween, makeRng, pick } from "./seed";
import { buildManualDocs as buildManualDocsFromCorpus } from "./manuals";

// Re-export the manual-doc types + builder so existing imports of
// `DocSource` / `ManualDoc` / `buildManualDocs` from this module keep
// compiling while the canonical data lives in `data/manuals.ts`.
export type DocSource = "manual" | "sensor_log";
export type ManualDoc = {
  id: string;
  titleEn: string;
  titleBn: string;
  tags: string[];
  bodyEn: string;
  bodyBn: string;
  source: DocSource;
};

/** What label the model surfaced on the captured frame. */
export type Anomaly = {
  id: string;
  sampleFile: string;
  anomalyLabelEn: string;
  anomalyLabelBn: string;
  repairStepsEn: string[];
  repairStepsBn: string[];
  /** 0..1 — the model's confidence in the anomaly + repair mapping. */
  confidence: number;
  /** Optional link to a machine id the run was attributed to. */
  machineId?: string;
  /** ISO 8601 timestamp. */
  createdAt: string;
};

// Bound to the same machine ids the factory store seeds (see
// `src/store/factory.store.ts`) so an Anomaly's machineId always resolves.
const MACHINE_POOL = [
  "m-1-1", "m-1-2", "m-1-3", "m-1-4", "m-1-5", "m-1-6",
  "m-2-1", "m-2-2", "m-2-3", "m-2-4", "m-2-5", "m-2-6",
  "m-3-1", "m-3-2", "m-3-3", "m-3-4", "m-3-5", "m-3-6"
];

type AnomalyTemplate = {
  sampleFile: string;
  anomalyLabelEn: string;
  anomalyLabelBn: string;
  repairStepsEn: string[];
  repairStepsBn: string[];
};

const ANOMALY_TEMPLATES: AnomalyTemplate[] = [
  {
    sampleFile: "defect-1-stitch-skip.jpg",
    anomalyLabelEn: "Stitch skip on shoulder seam",
    anomalyLabelBn: "কাঁধের সিমে সেলাই বাদ",
    repairStepsEn: [
      "Re-set the needle and confirm the thread path is clean.",
      "Reinforce with a second pass at 8 stitches/inch.",
      "Run the affected bundle through the QC station before packing."
    ],
    repairStepsBn: [
      "সুচ পুনঃস্থাপন করুন এবং সুতার পথ পরিষ্কার কিনা নিশ্চিত করুন।",
      "৮ সেলাই/ইঞ্চি হারে দ্বিতীয় পাস দিয়ে শক্তিবৃদ্ধি করুন।",
      "প্যাকিংয়ের আগে প্রভাবিত বান্ডলটি QC স্টেশনের মাধ্যমে চালান।"
    ]
  },
  {
    sampleFile: "defect-2-buttonhole.jpg",
    anomalyLabelEn: "Buttonhole fray at top edge",
    anomalyLabelBn: "বোতামের ছিদ্রের উপরের প্রান্তে ছেঁড়া",
    repairStepsEn: [
      "Trim loose threads and rebind with a Bartack #21 stitch.",
      "Check the cutter blade tension (target 2.4 N·m).",
      "Pull three samples from the lot; reject if more than 5% fail."
    ],
    repairStepsBn: [
      "ঢিলে সুতা ছেঁটে বারট্যাক #২১ সেলাই দিয়ে পুনঃবাঁধুন।",
      "কাটার ব্লেড টেনশন পরীক্ষা করুন (লক্ষ্য ২.৪ N·m)।",
      "লট থেকে তিনটি নমুনা সংগ্রহ করুন; ৫% এর বেশি ব্যর্থ হলে প্রত্যাখ্যান।"
    ]
  },
  {
    sampleFile: "defect-3-seam-pucker.jpg",
    anomalyLabelEn: "Seam pucker from uneven feed",
    anomalyLabelBn: "অসম ফিড থেকে সিমে ভাঁজ",
    repairStepsEn: [
      "Adjust the differential feed to 1.2.",
      "Steam-press before QC; reject puckers wider than 2 mm.",
      "Inspect the feed dog for wear; replace if teeth are flat."
    ],
    repairStepsBn: [
      "ডিফারেনশিয়াল ফিড ১.২ এ সমন্বয় করুন।",
      "QC এর আগে স্টিম-প্রেস করুন; ২ মিমির বেশি ভাঁজ প্রত্যাখ্যান।",
      "ফিড ডগ পরীক্ষা করুন; দাঁত চ্যাপ্টা হলে প্রতিস্থাপন।"
    ]
  },
  {
    sampleFile: "defect-4-fabric-stain.jpg",
    anomalyLabelEn: "Fabric stain near hem",
    anomalyLabelBn: "হেমের কাছে কাপড়ে দাগ",
    repairStepsEn: [
      "Spot-clean with neutral detergent at 30 °C.",
      "Reject the piece if the stain exceeds 5 mm in diameter.",
      "Log the lot id and machine id for traceability."
    ],
    repairStepsBn: [
      "৩০°C এ নিরপেক্ষ ডিটারজেন্ট দিয়ে স্পট-ক্লিন করুন।",
      "দাগ ৫ মিমি ব্যাসের বেশি হলে খণ্ডটি প্রত্যাখ্যান।",
      "ট্রেসেবিলিটির জন্য লট আইডি ও মেশিন আইডি লগ করুন।"
    ]
  }
];

export function buildVisionResults(seed = 0xC0FFEE_71, count = 6): Anomaly[] {
  const rng = makeRng(seed);
  const now = Date.now();
  const out: Anomaly[] = [];
  let id = 1;

  // Ensure determinism: pick templates cyclically, only pull one template per id.
  for (let i = 0; i < count; i++) {
    const tpl = ANOMALY_TEMPLATES[i % ANOMALY_TEMPLATES.length];
    const machineId = pick(rng, MACHINE_POOL);
    const confidence = Math.round((0.72 + rng() * 0.26) * 100) / 100;
    const createdAt = new Date(now - intBetween(rng, 5, 240) * 60_000).toISOString();

    out.push({
      id: `vis-${id++}`,
      sampleFile: tpl.sampleFile,
      anomalyLabelEn: tpl.anomalyLabelEn,
      anomalyLabelBn: tpl.anomalyLabelBn,
      repairStepsEn: [...tpl.repairStepsEn],
      repairStepsBn: [...tpl.repairStepsBn],
      confidence,
      machineId,
      createdAt
    });
  }

  return out;
}

const DOC_TEMPLATES: Array<Omit<ManualDoc, "id" | "createdAt">> = [
  // (NOTE: createdAt is added at runtime for parity with other audit-friendly docs.)
];

/** Re-export of the canonical manual corpus from `data/manuals.ts`. The
 *  canonical data lives there; we keep the legacy export here so existing
 *  consumers (e.g. `store/vision.store.ts`) don't have to change. */
export function buildManualDocs(): ManualDoc[] {
  return buildManualDocsFromCorpus();
}

// Quiet the "unused" warning for the placeholder array above; reserved for
// future append-only growth without forcing a schema change today.
void DOC_TEMPLATES;
