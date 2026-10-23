import { persist as storage } from "@/lib/persist";

export type RiskPolicy = {
  inventoryOrderThresholdBdt: number;
  autoExecuteCapBdt: number;
};

const defaultPolicy: RiskPolicy = {
  inventoryOrderThresholdBdt: 25000,
  autoExecuteCapBdt: 25000
};

const KEY = "bunonbrain:risk-policy";

export const riskService = {
  read(): RiskPolicy {
    return storage.get<RiskPolicy>(KEY, defaultPolicy);
  },
  update(patch: Partial<RiskPolicy>): RiskPolicy {
    const next = { ...this.read(), ...patch };
    storage.set(KEY, next);
    return next;
  },
  reset() {
    storage.set(KEY, defaultPolicy);
    return defaultPolicy;
  }
};