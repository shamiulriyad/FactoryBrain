// Manual knowledge corpus.
// ---------------------------------------------------------------------------
// Short RMG-flavoured manuals + sensor-log snippets that power the Ask page
// and the Manager agent tool path. Curated — no external vector DB in this
// build. The ask service ranks this corpus directly with a token-overlap
// ranking function in `src/services/factory.tools.ts::search_manual`.

import type { DocSourceT, ManualDocT } from "@/services/sensors.schemas";

/** Seed shape (kept separate from the published ManualDocT so we never
 *  expose `id`-less rows in the corpus). */
type Seed = Omit<ManualDocT, "id">;

const SEEDS: Seed[] = [
  {
    titleEn: "Bearing care checklist (sewing machines)",
    titleBn: "বিয়ারিং কেয়ার চেকলিস্ট (সেলাই মেশিন)",
    tags: ["maintenance", "sewing", "bearing"],
    bodyEn:
      "Weekly: check bearing temperature and vibration RMS with the handheld probe. Quarterly: re-grease with NLGI #2 lithium. Replace the bearing when vibration exceeds 4.5 mm/s or temperature crosses 75 °C for more than 30 minutes.",
    bodyBn:
      "সাপ্তাহিক: হ্যান্ডহেল্ড প্রোব দিয়ে বিয়ারিং তাপমাত্রা এবং কম্পন RMS পরীক্ষা করুন। ত্রৈমাসিক: NLGI #২ লিথিয়াম দিয়ে পুনর্গ্রিস করুন। কম্পন ৪.৫ mm/s অতিক্রম করলে বা তাপমাত্রা ৩০ মিনিটের বেশি ৭৫°C ছাড়িয়ে গেলে বিয়ারিং প্রতিস্থাপন করুন।",
    source: "manual"
  },
  {
    titleEn: "Line 3 efficiency decline — investigations",
    titleBn: "লাইন ৩ দক্ষতা হ্রাস — তদন্ত",
    tags: ["line-3", "efficiency", "investigation"],
    bodyEn:
      "Sustained 12–18% dip vs target over the last 4 hours. Check sewing helper allocation first, then QC backlog. Sensor log pattern: bundle_scan rate dropped from 18 to 11/min between 09:00–11:00.",
    bodyBn:
      "গত ৪ ঘন্টায় লক্ষ্যের তুলনায় টানা ১২–১৮% হ্রাস। প্রথমে সেলাই সহায়ক বরাদ্দ, তারপর QC ব্যাকলগ পরীক্ষা করুন। সেন্সর লগ প্যাটার্ন: ০৯:০০–১১:০০ এর মধ্যে বান্ডল_স্ক্যান হার মিনিটে ১৮ থেকে ১১ এ নেমেছে।",
    source: "sensor_log"
  },
  {
    titleEn: "Buttonhole machine — calibration",
    titleBn: "বোতামের ছিদ্র মেশিন — ক্যালিব্রেশন",
    tags: ["finishing", "buttonhole", "calibration"],
    bodyEn:
      "Cutter blade tension target: 2.4 N·m. Re-calibrate after every 5,000 cycles. When binding rate exceeds 2% over a shift, pull two random samples and inspect under the loupe.",
    bodyBn:
      "কাটার ব্লেড টেনশন লক্ষ্য: ২.৪ N·m। প্রতি ৫,০০০ সাইকেলের পর পুনঃক্যালিব্রেট করুন। শিফটে বাইন্ডিং হার ২% ছাড়ালে দুটি এলোমেলো নমুনা তুলে লুপের নিচে পরীক্ষা করুন।",
    source: "manual"
  },
  {
    titleEn: "Bundles per minute — target table by SMV",
    titleBn: "বান্ডল প্রতি মিনিট — SMV অনুযায়ী লক্ষ্য",
    tags: ["throughput", "sah", "smv"],
    bodyEn:
      "Standard SAH target: 22 bundles/min at SMV 0.45, scaling linearly to 13 bundles/min at SMV 0.75. If observed rate is below 80% of target for 30+ minutes, pull helper allocation from Finishing and rebalance to Sewing.",
    bodyBn:
      "স্ট্যান্ডার্ড SAH লক্ষ্য: SMV ০.৪৫ এ ২২ বান্ডল/মিনিট, SMV ০.৭৫ এ রৈখিকভাবে ১৩ বান্ডল/মিনিট। ৩০+ মিনিটের জন্য লক্ষ্যের ৮০% এর নিচে দেখলে ফিনিশিং থেকে সেলাইতে সহায়ক পুনর্বণ্টন করুন।",
    source: "manual"
  },
  {
    titleEn: "Energy spike on Line 4 compressor — log",
    titleBn: "লাইন ৪ কম্প্রেসর শক্তি স্পাইক — লগ",
    tags: ["line-4", "energy", "compressor"],
    bodyEn:
      "Energy meter on compressor Line 4: 22% above 7-day rolling average. Peak demand 165 kW vs baseline 130 kW. Pattern starts after 14:00 daily — correlate with finishing-station dryer cycle.",
    bodyBn:
      "কম্প্রেসর লাইন ৪ এর শক্তি মিটার: ৭ দিনের চলমান গড়ের চেয়ে ২২% বেশি। পিক ডিমান্ড ১৬৫ kW বনাম বেসলাইন ১৩০ kW। দৈনিক ১৪:০০ এর পরে প্যাটার্ন শুরু — ফিনিশিং-স্টেশন ড্রায়ার সাইকেলের সাথে সম্পর্কযুক্ত।",
    source: "sensor_log"
  },
  {
    titleEn: "QC reject categories & thresholds",
    titleBn: "QC প্রত্যাখ্যান বিভাগ ও থ্রেশহোল্ড",
    tags: ["qc", "rejects", "defects"],
    bodyEn:
      "Reject categories: stitch skip (> 3 mm gap), open seam (> 5 mm), oil stain (> 5 mm), shading, measurement out-of-tolerance (> 4 mm). If a category spikes by >25% week-on-week, file a defect cluster insight and reroute the next two PO bundles to inline QC.",
    bodyBn:
      "প্রত্যাখ্যান বিভাগ: সেলাই বাদ (> ৩ মিমি ফাঁক), খোলা সিম (> ৫ মিমি), তেলের দাগ (> ৫ মিমি), শেডিং, পরিমাপ সহনশীলতার বাইরে (> ৪ মিমি)। কোনো বিভাগ সপ্তাহে ২৫% এর বেশি বাড়লে একটি ত্রুটি ক্লাস্টার অন্তর্দৃষ্টি খুলুন এবং পরবর্তী দুটি PO বান্ডল ইনলাইন QC-তে ররাউট করুন।",
    source: "manual"
  },
  {
    titleEn: "Cutting table — fabric relaxation time",
    titleBn: "কাটিং টেবিল — কাপড় রিল্যাক্সেশন সময়",
    tags: ["cutting", "fabric", "preparation"],
    bodyEn:
      "Allow knit fabric to relax for 12–24 hours before cutting. Shrinkage of 3–5% is expected. Spreading on the cutting table: align selvage, face up for the top ply, and pre-spot any knot/stain.",
    bodyBn:
      "কাটিংয়ের আগে নিট কাপড়কে ১২–২৪ ঘন্টা রিল্যাক্স করতে দিন। ৩–৫% সংকোচন প্রত্যাশিত। কাটিং টেবিলে ছড়ানো: সেলভেজ সারিবদ্ধ করুন, উপরের স্তর মুখ উপরে, এবং যেকোনো গিঁট/দাগ আগে চিহ্নিত করুন।",
    source: "manual"
  },
  {
    titleEn: "M-101 vibration trend — last 24h",
    titleBn: "M-101 কম্পন প্রবণতা — শেষ ২৪ ঘন্টা",
    tags: ["m-101", "machine", "vibration", "investigation"],
    bodyEn:
      "Vibration RMS on M-101 climbed from 2.1 mm/s (06:00) to 3.9 mm/s (17:00). Temperature stable at 64 °C. Noise floor unchanged. Likely cause: feed dog wear — schedule bearing inspection within 72h.",
    bodyBn:
      "M-101 এ কম্পন RMS ০৬:০০ এ ২.১ mm/s থেকে ১৭:০০ এ ৩.৯ mm/s এ উঠেছে। তাপমাত্রা ৬৪°C এ স্থিতিশীল। নয়েজ ফ্লোর অপরিবর্তিত। সম্ভাব্য কারণ: ফিড ডগ ক্ষয় — ৭২ ঘন্টার মধ্যে বিয়ারিং পরীক্ষা শিডিউল করুন।",
    source: "sensor_log"
  }
];

/** Build the canonical manual corpus (immutable). */
export function buildManualDocs(): ManualDocT[] {
  return SEEDS.map((d, i) => ({
    ...d,
    id: `doc-${i + 1}`
  }));
}

/** Same corpus, but with `tags` already lowercased so callers can do a
 *  cheap `tag.includes(...)` lookup without re-normalising each time. */
export function getManualCorpus(): ManualDocT[] {
  return buildManualDocs().map((d) => ({
    ...d,
    tags: d.tags.map((t) => t.toLowerCase())
  }));
}
