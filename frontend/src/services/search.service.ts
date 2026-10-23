// Unified search index. Builds from dataset + live services so any update
// to the store or activity log flows through here automatically.

import { dataset } from "./dataset";
import { activityService } from "./activity.service";
import { useBusinessStore } from "@/store/business.store";

export type SearchKind =
  | "page"
  | "agent"
  | "insight"
  | "approval"
  | "activity"
  | "order"
  | "buyer"
  | "supplier"
  | "compliance"
  | "process"
  | "target"
  | "risk"
  | "integration"
  | "setting";

export type SearchItem = {
  /** Stable id within its kind. */
  id: string;
  /** Search kind, used for grouping + i18n label. */
  kind: SearchKind;
  /** Title in user's locale (en or bn — picked at render time). */
  title: string;
  /** Secondary line. */
  hint: string;
  /** Optional pre-localised variants so locale switching re-renders with no re-index. */
  titleBn: string;
  hintBn: string;
  /** Deep link. Use focus / node / section query params where applicable. */
  href: string;
  /** Free-form match weights — higher = more relevant. */
  weight: number;
  /** When set, the matched haystack substring for highlight. */
  matchedField?: "title" | "hint";
};

const PAGE_HINTS_EN: Record<string, { title: string; hint: string; titleBn: string; hintBn: string }> = {
  "/app": {
    title: "Overview",
    hint: "KPIs · important today · recent activity",
    titleBn: "সারসংক্ষেপ",
    hintBn: "KPI · আজ গুরুত্বপূর্ণ · সাম্প্রতিক কার্যকলাপ"
  },
  "/app/ask": {
    title: "Ask BunonBrain",
    hint: "Natural-language answers with evidence",
    titleBn: "বুননব্রেইনকে জিজ্ঞাসা করুন",
    hintBn: "প্রমাণসহ প্রাকৃতিক ভাষার উত্তর"
  },
  "/app/brain": {
    title: "Factory Brain",
    hint: "Graph of lines, machines, orders, suppliers, risks",
    titleBn: "ফ্যাক্টরি ব্রেইন",
    hintBn: "লাইন, মেশিন, অর্ডার, সরবরাহকারী ও ঝুঁকির গ্রাফ"
  },
  "/app/insights": {
    title: "Insights",
    hint: "Filter by stage, agent and risk",
    titleBn: "অন্তর্দৃষ্টি",
    hintBn: "পর্যায়, এজেন্ট ও ঝুঁকি দিয়ে ফিল্টার"
  },
  "/app/qc": {
    title: "Quality Control",
    hint: "Defect and rework rates by operation and line",
    titleBn: "মান নিয়ন্ত্রণ",
    hintBn: "অপারেশন ও লাইন অনুযায়ী ত্রুটি ও রি-ওয়ার্ক হার"
  },
  "/app/vision": {
    title: "Vision",
    hint: "Camera-based line diagnostics and defect detection",
    titleBn: "ভিশন",
    hintBn: "ক্যামেরা-ভিত্তিক লাইন ডায়াগনস্টিক ও ত্রুটি সনাক্তকরণ"
  },
  "/app/agents": {
    title: "Workforce",
    hint: "The three agents assembled for your factory",
    titleBn: "কর্মী বাহিনী",
    hintBn: "আপনার কারখানার জন্য গঠিত তিনটি এজেন্ট"
  },
  "/app/approvals": {
    title: "Approvals",
    hint: "High-risk actions awaiting your decision",
    titleBn: "অনুমোদন",
    hintBn: "উচ্চ-ঝুঁকির পদক্ষেপ আপনার সিদ্ধান্তের অপেক্ষায়"
  },
  "/app/activity": {
    title: "Activity",
    hint: "Audit log of every decision and update",
    titleBn: "কার্যকলাপ",
    hintBn: "প্রতিটি সিদ্ধান্ত ও আপডেটের অডিট লগ"
  },
  "/app/integrations": {
    title: "Integrations",
    hint: "RFID · machine telemetry · energy · Documents",
    titleBn: "ইন্টিগ্রেশন",
    hintBn: "RFID · মেশিন টেলিমেট্রি · এনার্জি · নথি"
  },
  "/app/settings": {
    title: "Settings",
    hint: "Language · autonomy · risk · appearance",
    titleBn: "সেটিংস",
    hintBn: "ভাষা · স্বায়ত্তশাসন · ঝুঁকি · চেহারা"
  }
};

const SETTINGS: Array<{ id: string; title: string; hint: string; titleBn: string; hintBn: string; section: string }> = [
  { id: "settings:language", title: "Language", hint: "Switch interface language", titleBn: "ভাষা", hintBn: "ইন্টারফেসের ভাষা", section: "appearance" },
  { id: "settings:theme", title: "Theme", hint: "Light, dark, or system", titleBn: "থিম", hintBn: "হালকা, গাঢ় বা সিস্টেম", section: "appearance" },
  { id: "settings:density", title: "Density", hint: "Comfortable or compact layout", titleBn: "ঘনত্ব", hintBn: "আরামদায়ক বা কম্প্যাক্ট", section: "appearance" },
  { id: "settings:businessName", title: "Business name", hint: "Shown in greetings and exports", titleBn: "ব্যবসার নাম", hintBn: "অভিবাদন ও এক্সপোর্টে দেখানো হয়", section: "profile" },
  { id: "settings:city", title: "City", hint: "Headquarters location", titleBn: "শহর", hintBn: "সদর দপ্তরের অবস্থান", section: "profile" },
  { id: "settings:agentMode", title: "Per-agent autonomy", hint: "Auto, approval, paused", titleBn: "প্রতি এজেন্ট স্বায়ত্তশাসন", hintBn: "স্বয়ংক্রিয়, অনুমোদন, বিরত", section: "agents" },
  { id: "settings:globalPause", title: "Pause all agents", hint: "Stops every agent immediately", titleBn: "সব এজেন্ট বিরত", hintBn: "প্রতিটি এজেন্ট সাথে সাথে বন্ধ", section: "agents" },
  { id: "settings:thresholds", title: "Risk thresholds", hint: "Order value, discount, spend, volume", titleBn: "ঝুঁকি সীমা", hintBn: "অর্ডার মূল্য, ছাড়, খরচ, ভলিউম", section: "risk" },
  { id: "settings:autoApprove", title: "Auto-approve below", hint: "Skip approval under a risk tier", titleBn: "নিচে স্বয়ংক্রিয় অনুমোদন", hintBn: "একটি ঝুঁকি স্তরের নিচে অনুমোদন এড়িয়ে যান", section: "risk" },
  { id: "settings:approvalGate", title: "Approval gate", hint: "Which risk tiers need approval", titleBn: "অনুমোদন গেট", hintBn: "কোন ঝুঁকি স্তরের অনুমোদন প্রয়োজন", section: "risk" },
  { id: "settings:exportConfig", title: "Export configuration", hint: "Download a JSON snapshot", titleBn: "কনফিগারেশন এক্সপোর্ট", hintBn: "JSON স্ন্যাপশট ডাউনলোড", section: "data" },
  { id: "settings:importConfig", title: "Import configuration", hint: "Restore from a JSON snapshot", titleBn: "কনফিগারেশন ইম্পোর্ট", hintBn: "JSON স্ন্যাপশট থেকে পুনরুদ্ধার", section: "data" },
  { id: "settings:resetDemo", title: "Reset demo", hint: "Clear everything and start over", titleBn: "ডেমো রিসেট", hintBn: "সবকিছু মুছে আবার শুরু", section: "dataReset" }
];

const SOURCE_LABELS: Record<string, { en: string; bn: string; hintEn: string; hintBn: string }> = {
  sheets: { en: "Google Sheets", bn: "গুগল শীটস", hintEn: "Spreadsheet import", hintBn: "স্প্রেডশীট ইম্পোর্ট" },
  whatsapp: { en: "WhatsApp", bn: "হোয়াটসঅ্যাপ", hintEn: "Conversations", hintBn: "কথোপকথন" },
  csv: { en: "CSV / Excel", bn: "CSV / এক্সেল", hintEn: "File upload or Drive link", hintBn: "ফাইল আপলোড বা ড্রাইভ লিঙ্ক" },
  documents: { en: "Documents", bn: "নথি", hintEn: "Policies · supplier contracts", hintBn: "নীতি · সরবরাহকারী চুক্তি" }
};

export const searchService = {
  /** Build a fresh index. Cheap (already in-memory); we re-build on every query
      so live activity / approval changes are reflected without a manual refresh. */
  index(): SearchItem[] {
    const out: SearchItem[] = [];
    const sources = useBusinessStore.getState().sources;

    // Pages
    for (const [href, meta] of Object.entries(PAGE_HINTS_EN)) {
      out.push({
        id: `page:${href}`,
        kind: "page",
        title: meta.title,
        hint: meta.hint,
        titleBn: meta.titleBn,
        hintBn: meta.hintBn,
        href,
        weight: 0.5
      });
    }

    // Agents
    for (const a of dataset.agents) {
      out.push({
        id: `agent:${a.id}`,
        kind: "agent",
        title: a.name,
        hint: a.purpose,
        titleBn: a.nameBn,
        hintBn: a.purposeBn,
        href: `/app/agents?focus=${a.id}`,
        weight: 0.7
      });
    }

    // Insights (high weight)
    for (const i of dataset.insights) {
      out.push({
        id: `insight:${i.id}`,
        kind: "insight",
        title: i.title,
        hint: i.recommendation.action,
        titleBn: i.titleBn,
        hintBn: i.recommendation.actionBn,
        href: `/app/insights?focus=${i.id}`,
        weight: 0.85
      });
      if (i.stage === "pending_approval") {
        out.push({
          id: `approval:${i.id}`,
          kind: "approval",
          title: i.title,
          hint: `${i.agentLabel} · ${i.recommendation.riskTier}`,
          titleBn: i.titleBn,
          hintBn: `${i.agentLabel} · ${i.recommendation.riskTier}`,
          href: `/app/approvals?focus=${i.id}`,
          weight: 0.9
        });
      }
    }

    // Activity (latest 25)
    const act = activityService.recent({ limit: 25 });
    for (const a of act) {
      out.push({
        id: `activity:${a.id}`,
        kind: "activity",
        title: `${a.actorLabel}: ${a.verb}${a.target ? " · " + a.target : ""}`,
        hint: a.outcome ?? "",
        titleBn: `${a.actorLabel}: ${a.verbBn}${a.targetBn ? " · " + a.targetBn : ""}`,
        hintBn: a.outcome ?? "",
        href: "/app/activity",
        weight: 0.55
      });
    }

    // Products (top 60 by name prominence — already in dataset)
    for (const p of dataset.products.slice(0, 60)) {
      out.push({
        id: `product:${p.id}`,
        kind: "order",
        title: p.name,
        hint: `${p.category} · ${p.sku}`,
        titleBn: p.nameBn,
        hintBn: `${p.category} · ${p.sku}`,
        href: `/app/brain?node=${p.id}`,
        weight: 0.5
      });
    }
    // Buyers (top 25 by LTV — picked from graph)
    const buyerNodes = dataset.graph.nodes.filter((n) => n.kind === "buyer");
    for (const c of buyerNodes.slice(0, 25)) {
      out.push({
        id: `buyer:${c.id}`,
        kind: "buyer",
        title: c.label,
        hint: `${c.meta?.region ?? ""} · LTV ৳${(c.meta?.ltv ?? 0).toLocaleString("en-IN")}`,
        titleBn: c.label,
        hintBn: `${c.meta?.region ?? ""} · LTV ৳${(c.meta?.ltv ?? 0).toLocaleString("en-IN")}`,
        href: `/app/brain?node=${c.id}`,
        weight: 0.55
      });
    }
    // Suppliers
    for (const s of dataset.suppliers) {
      out.push({
        id: `supplier:${s.id}`,
        kind: "supplier",
        title: s.name,
        hint: `${s.region} · lead ${s.leadTimeDays}d · ${Math.round(s.onTimeRate * 100)}% on-time`,
        titleBn: s.name,
        hintBn: `${s.region} · লিড ${s.leadTimeDays}দিন · ${Math.round(s.onTimeRate * 100)}% সময়মতো`,
        href: `/app/brain?node=${s.id}`,
        weight: 0.55
      });
    }
    // Policies
    for (const p of dataset.policies) {
      out.push({
        id: `policy:${p.id}`,
        kind: "compliance",
        title: p.title,
        hint: p.type.replace(/-/g, " "),
        titleBn: p.titleBn,
        hintBn: p.type === "supplier-agreement" ? "সরবরাহকারী চুক্তি" : p.type === "return" ? "ফেরত নীতি" : p.type === "shipping" ? "শিপিং" : "গোপনীয়তা",
        href: `/app/brain?node=${p.id}`,
        weight: 0.6
      });
    }
    // Processes + targets + risks (from graph)
    for (const n of dataset.graph.nodes) {
      if (n.kind === "process") {
        out.push({
          id: `process:${n.id}`,
          kind: "process",
          title: n.label,
          hint: "Process",
          titleBn: n.labelBn,
          hintBn: "প্রক্রিয়া",
          href: `/app/brain?node=${n.id}`,
          weight: 0.5
        });
      } else if (n.kind === "target") {
        out.push({
          id: `target:${n.id}`,
          kind: "target",
          title: n.label,
          hint: "Production target",
          titleBn: n.labelBn,
          hintBn: "উৎপাদন লক্ষ্য",
          href: `/app/brain?node=${n.id}`,
          weight: 0.55
        });
      } else if (n.kind === "risk") {
        out.push({
          id: `risk:${n.id}`,
          kind: "risk",
          title: n.label,
          hint: `Risk · ${n.meta?.severity ?? "tracked"}`,
          titleBn: n.labelBn,
          hintBn: `ঝুঁকি · ${n.meta?.severity ?? "ট্র্যাকড"}`,
          href: `/app/brain?node=${n.id}`,
          weight: 0.7
        });
      }
    }

    // Integrations (sources) — skip out_of_scope social noise in ⌘K
    for (const s of sources) {
      if (s.category === "out_of_scope") continue;
      const lbl = SOURCE_LABELS[s.id];
      if (!lbl) continue;
      out.push({
        id: `integration:${s.id}`,
        kind: "integration",
        title: lbl.en,
        hint: s.connected ? `Connected · ${s.records.toLocaleString("en-IN")} records` : lbl.hintEn,
        titleBn: lbl.bn,
        hintBn: s.connected ? `সংযুক্ত · ${s.records.toLocaleString("en-IN")} রেকর্ড` : lbl.hintBn,
        href: "/app/integrations",
        weight: 0.5
      });
    }

    // Settings
    for (const s of SETTINGS) {
      out.push({
        id: s.id,
        kind: "setting",
        title: s.title,
        hint: s.hint,
        titleBn: s.titleBn,
        hintBn: s.hintBn,
        href: `/app/settings?section=${s.section}`,
        weight: 0.5
      });
    }

    return out;
  },

  /** Run a fuzzy match. Scoring:
      1. Substring on title (highest)
      2. Substring on hint
      3. Per-character ordered subsequence fallback (so "hl" hits "Hello")
      Empty query returns [] (the palette renders suggestions separately). */
  search(query: string, locale: "en" | "bn", limit = 24): SearchItem[] {
    const q = query.trim().toLowerCase();
    if (!q) return [];
    const items = this.index();
    const scored: Array<{ item: SearchItem; score: number }> = [];
    for (const it of items) {
      const title = (locale === "bn" ? it.titleBn : it.title).toLowerCase();
      const hint = (locale === "bn" ? it.hintBn : it.hint).toLowerCase();
      let score = 0;
      let matched: "title" | "hint" | undefined;
      if (title.startsWith(q)) score += 100;
      if (title.includes(q)) {
        score += 60;
        matched = "title";
      }
      if (hint.includes(q)) {
        score += 30;
        if (!matched) matched = "hint";
      }
      // Subsequence fallback
      if (score === 0 && q.length >= 2) {
        let i = 0;
        let ok = true;
        for (const ch of title) {
          if (ch === q[i]) i++;
          if (i === q.length) break;
        }
        if (i === q.length) {
          score += 12;
          matched = "title";
        }
      }
      if (score === 0) continue;
      scored.push({ item: { ...it, matchedField: matched }, score: score * it.weight });
    }
    scored.sort((a, b) => b.score - a.score);
    return scored.slice(0, limit).map((s) => s.item);
  },

  /** Group search results by kind, preserving score order. */
  group(items: SearchItem[]): Array<{ kind: SearchKind; items: SearchItem[] }> {
    const map = new Map<SearchKind, SearchItem[]>();
    for (const it of items) {
      if (!map.has(it.kind)) map.set(it.kind, []);
      map.get(it.kind)!.push(it);
    }
    return Array.from(map.entries()).map(([kind, items]) => ({ kind, items }));
  },

  /** Default suggestions shown when the query box is empty.
      Picked from the most prominent items in the index. */
  suggestions(locale: "en" | "bn"): string[] {
    const en = [
      "line throughput",
      "Line 3 efficiency",
      "machine health",
      "compressor energy",
      "Higg FEM",
      "Line Throughput"
    ];
    const bn = [
      "লাইন দক্ষতা",
      "লাইন ৩ দক্ষতা",
      "মেশিন স্বাস্থ্য",
      "কম্প্রেসর শক্তি",
      "হিগ ফেম",
      "লাইন থ্রুপুট"
    ];
    return (locale === "bn" ? bn : en).slice();
  }
};
