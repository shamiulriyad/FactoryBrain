import { intBetween, makeRng, pick, range } from "./seed";

export type Region =
  | "Dhaka"
  | "Chattogram"
  | "Sylhet"
  | "Khulna"
  | "Rajshahi"
  | "Barishal"
  | "Rangpur"
  | "Mymensingh";

export type Customer = {
  id: string;
  name: string;
  region: Region;
  firstOrderDays: number; // days ago
  totalOrders: number;
  ltvBdt: number;
  churnRisk: number; // 0..1
  repeatBuyer: boolean;
  preferredCategory: string;
  lastOrderDays: number;
};

const firstNames = ["Rahim", "Karim", "Sumaiya", "Tania", "Nusrat", "Shahin", "Rumi", "Sadia", "Imran", "Lamia", "Tariq", "Mahi", "Rina", "Faisal", "Mithila", "Tahmid", "Raisa", "Adib", "Priya", "Sabbir"];
const lastNames = ["Hossain", "Akter", "Rahman", "Begum", "Islam", "Chowdhury", "Khan", "Miah", "Sultana", "Sarkar", "Alam", "Bhuiyan", "Haque", "Mondal", "Talukder"];

export function buildCustomers(count = 4218): Customer[] {
  const rng = makeRng(0xCAFE_0001);
  const regions: Region[] = ["Dhaka", "Chattogram", "Sylhet", "Khulna", "Rajshahi", "Barishal", "Rangpur", "Mymensingh"];
  return range(count, (i) => {
    const totalOrders = pick(rng, [
      Math.max(1, Math.round(intBetween(rng, 1, 3))),
      Math.max(1, Math.round(intBetween(rng, 1, 5))),
      Math.max(2, Math.round(intBetween(rng, 2, 6)))
    ]);
    const avgTicket = intBetween(rng, 800, 4800);
    const ltvBdt = totalOrders * avgTicket;
    const repeatBuyer = totalOrders >= 2;
    const lastOrderDays = intBetween(rng, 1, 90);
    // Higher recent inactivity → higher churn risk
    const inactivityFactor = Math.min(1, lastOrderDays / 90);
    const recencyFactor = totalOrders >= 4 ? 0.2 : 0.5;
    const churnRisk = Math.min(1, Math.max(0, inactivityFactor * 0.7 + recencyFactor - rng() * 0.2));
    return {
      id: `c-${(i + 1).toString().padStart(5, "0")}`,
      name: `${pick(rng, firstNames)} ${pick(rng, lastNames)}`,
      region: pick(rng, regions),
      firstOrderDays: intBetween(rng, 30, 720),
      totalOrders,
      ltvBdt,
      churnRisk,
      repeatBuyer,
      preferredCategory: pick(rng, ["Apparel", "Beauty", "Home", "Grocery", "Electronics", "Footwear", "Accessories", "Kids"]),
      lastOrderDays
    };
  });
}