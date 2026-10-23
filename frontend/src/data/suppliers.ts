export type Supplier = {
  id: string;
  name: string;
  region: string;
  leadTimeDays: number;
  onTimeRate: number; // 0..1
  contractRef: string;
};

export const suppliers: Supplier[] = [
  { id: "sup-1", name: "Narayanganj Textiles", region: "Narayanganj", leadTimeDays: 7, onTimeRate: 0.92, contractRef: "policy:supplier-agreement-1" },
  { id: "sup-2", name: "Old Dhaka Spice Co.", region: "Dhaka", leadTimeDays: 4, onTimeRate: 0.88, contractRef: "policy:supplier-agreement-2" },
  { id: "sup-3", name: "Chattogram Leather", region: "Chattogram", leadTimeDays: 10, onTimeRate: 0.81, contractRef: "policy:supplier-agreement-3" },
  { id: "sup-4", name: "Sylhet Tea & Beauty", region: "Sylhet", leadTimeDays: 5, onTimeRate: 0.95, contractRef: "policy:supplier-agreement-1" },
  { id: "sup-5", name: "Khulna Jute Works", region: "Khulna", leadTimeDays: 9, onTimeRate: 0.78, contractRef: "policy:supplier-agreement-2" },
  { id: "sup-6", name: "Rajshahi Mango Co-op", region: "Rajshahi", leadTimeDays: 3, onTimeRate: 0.93, contractRef: "policy:supplier-agreement-3" },
  { id: "sup-7", name: "Barishal Coastal Goods", region: "Barishal", leadTimeDays: 12, onTimeRate: 0.74, contractRef: "policy:supplier-agreement-1" },
  { id: "sup-8", name: "Rangpur Cold Chain", region: "Rangpur", leadTimeDays: 8, onTimeRate: 0.86, contractRef: "policy:supplier-agreement-2" },
  { id: "sup-9", name: "Mymensingh Silk Loom", region: "Mymensingh", leadTimeDays: 14, onTimeRate: 0.83, contractRef: "policy:supplier-agreement-3" },
  { id: "sup-10", name: "Gazipur Apparel Hub", region: "Gazipur", leadTimeDays: 6, onTimeRate: 0.90, contractRef: "policy:supplier-agreement-1" },
  { id: "sup-11", name: "Comilla Ceramics", region: "Comilla", leadTimeDays: 11, onTimeRate: 0.79, contractRef: "policy:supplier-agreement-2" },
  { id: "sup-12", name: "Jessore Handicrafts", region: "Jessore", leadTimeDays: 9, onTimeRate: 0.84, contractRef: "policy:supplier-agreement-3" }
];