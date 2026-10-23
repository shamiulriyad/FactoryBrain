import { intBetween, makeRng, pick, range } from "./seed";

export type Category =
  | "Apparel"
  | "Beauty"
  | "Electronics"
  | "Home"
  | "Grocery"
  | "Footwear"
  | "Accessories"
  | "Kids";

export type Product = {
  id: string;
  sku: string;
  name: string;
  nameBn: string;
  category: Category;
  costBdt: number;
  priceBdt: number;
  supplierId: string;
  leadTimeDays: number;
};

const categoryPrefixes: Record<Category, string[]> = {
  Apparel: ["Heritage", "Coral", "Bengal", "Urban", "Festive", "Summer"],
  Beauty: ["Glow", "Aura", "Pure", "Sundari", "Night", "Verdant"],
  Electronics: ["Volt", "Pulse", "Orbit", "Bolt", "Echo", "Quantum"],
  Home: ["Hearth", "Cedar", "Banyan", "Stone", "Riverside", "Plaza"],
  Grocery: ["Harvest", "Spice", "Field", "Riverbed", "Saffron", "Mango"],
  Footwear: ["Stride", "Cobbler", "Ranger", "Trail", "Mariner", "Coast"],
  Accessories: ["Loom", "Silk", "Onyx", "Pearl", "Thread", "Bangle"],
  Kids: ["Cub", "Spark", "Tiny", "Kite", "Bubbles", "Mango"]
};

const suffixes = ["Lite", "Pro", "Classic", "Max", "Mini", "Plus"];

export function buildProducts(count = 342): Product[] {
  const rng = makeRng(0xC0DE_0001);
  const categories: Category[] = [
    "Apparel",
    "Beauty",
    "Electronics",
    "Home",
    "Grocery",
    "Footwear",
    "Accessories",
    "Kids"
  ];
  return range(count, (i) => {
    const cat = pick(rng, categories);
    const prefix = pick(rng, categoryPrefixes[cat]);
    const suffix = pick(rng, suffixes);
    const name = `${prefix} ${cat} ${suffix}`;
    const nameBn = translateName(name);
    const costBdt = intBetween(rng, 120, 1800);
    const margin = intBetween(rng, 28, 65) / 100;
    const priceBdt = Math.round((costBdt / (1 - margin)) / 10) * 10;
    const supplierId = `sup-${intBetween(rng, 1, 12)}`;
    const leadTimeDays = intBetween(rng, 3, 21);
    return {
      id: `p-${(i + 1).toString().padStart(4, "0")}`,
      sku: `${cat.slice(0, 3).toUpperCase()}-${(i + 1).toString().padStart(4, "0")}`,
      name,
      nameBn,
      category: cat,
      costBdt,
      priceBdt,
      supplierId,
      leadTimeDays
    };
  });
}

function translateName(en: string): string {
  // Light transliteration for demo; product names shown in both languages.
  const map: Record<string, string> = {
    Heritage: "ঐতিহ্য",
    Coral: "প্রবাল",
    Bengal: "বাংলা",
    Urban: "নগর",
    Festive: "উৎসব",
    Summer: "গ্রীষ্ম",
    Glow: "আভা",
    Aura: "করোটি",
    Pure: "বিশুদ্ধ",
    Sundari: "সুন্দরী",
    Night: "রাত",
    Verdant: "সবুজ",
    Volt: "ভোল্ট",
    Pulse: "স্পন্দন",
    Orbit: "কক্ষ",
    Bolt: "বিদ্যুৎ",
    Echo: "প্রতিধ্বনি",
    Quantum: "কোয়ান্টাম",
    Hearth: "আঁচন",
    Cedar: "দেবদারু",
    Banyan: "বট",
    Stone: "পাথর",
    Riverside: "নদীতীর",
    Plaza: "প্লাজা",
    Harvest: "ফসল",
    Spice: "মশলা",
    Field: "ক্ষেত",
    Riverbed: "নদীগর্ভ",
    Saffron: "জাফরান",
    Mango: "আম",
    Stride: "পদক্ষেপ",
    Cobbler: "মুচি",
    Ranger: "বনরক্ষী",
    Trail: "পথ",
    Mariner: "নাবিক",
    Coast: "উপকূল",
    Loom: "তাঁত",
    Silk: "রেশম",
    Onyx: "ওনিক্স",
    Pearl: "মুক্তা",
    Thread: "সুতা",
    Bangle: "চুড়ি",
    Cub: "ছানা",
    Spark: "চমক",
    Tiny: "ক্ষুদে",
    Kite: "ঘুড়ি",
    Bubbles: "বুদবুদ",
    Apparel: "পোশাক",
    Beauty: "সৌন্দর্য",
    Electronics: "ইলেকট্রনিক্স",
    Home: "ঘর",
    Grocery: "মুদি",
    Footwear: "জুতা",
    Accessories: "আনুষঙ্গিক",
    Kids: "শিশু",
    Lite: "লাইট",
    Pro: "প্রো",
    Classic: "ক্লাসিক",
    Max: "ম্যাক্স",
    Mini: "মিনি",
    Plus: "প্লাস"
  };
  return en
    .split(" ")
    .map((w) => map[w] ?? w)
    .join(" ");
}