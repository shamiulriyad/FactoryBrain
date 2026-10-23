import { dataset } from "./dataset";
import type { Health } from "./types";

export const metricService = {
  health(): Health {
    return dataset.health;
  }
};