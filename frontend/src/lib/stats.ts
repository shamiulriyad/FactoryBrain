// Plain statistical utilities for forecasting (Holt-Winters, moving average, baseline).
// Used by the Inventory Agent. NOT an LLM.

export function mean(arr: number[]) {
  if (!arr.length) return 0;
  return arr.reduce((a, b) => a + b, 0) / arr.length;
}

export function movingAverage(arr: number[], window: number) {
  const out: number[] = [];
  for (let i = 0; i < arr.length; i++) {
    const start = Math.max(0, i - window + 1);
    const slice = arr.slice(start, i + 1);
    out.push(mean(slice));
  }
  return out;
}

// Holt-Winters additive triple exponential smoothing.
// seasonLength default 7 (weekly). Forecasts horizon steps ahead.
export function holtWinters(
  series: number[],
  options: { alpha?: number; beta?: number; gamma?: number; seasonLength?: number; horizon: number }
) {
  const { alpha = 0.3, beta = 0.1, gamma = 0.2, seasonLength = 7, horizon } = options;
  const n = series.length;
  if (n < seasonLength * 2) {
    return { forecast: Array(horizon).fill(mean(series)), fitted: [] as number[] };
  }
  // Initial level = mean of first season
  let level = mean(series.slice(0, seasonLength));
  // Initial trend
  let trend = (mean(series.slice(seasonLength, seasonLength * 2)) - level) / seasonLength;
  // Initial seasonal indices
  const seasonals: number[] = Array(seasonLength).fill(0);
  for (let i = 0; i < seasonLength * 2; i++) {
    seasonals[i % seasonLength] += (series[i] - level) / seasonLength;
  }

  const fitted: number[] = [];
  for (let i = 0; i < n; i++) {
    const s = seasonals[i % seasonLength];
    const value = series[i];
    const newLevel = alpha * (value - s) + (1 - alpha) * (level + trend);
    const newTrend = beta * (newLevel - level) + (1 - beta) * trend;
    seasonals[i % seasonLength] = gamma * (value - newLevel) + (1 - gamma) * s;
    level = newLevel;
    trend = newTrend;
    fitted.push(level + trend + s);
  }

  const forecast: number[] = [];
  for (let h = 1; h <= horizon; h++) {
    forecast.push(level + h * trend + seasonals[(n + h - 1) % seasonLength]);
  }
  return { forecast, fitted };
}

// Simple baseline (e.g. 30-day rolling mean)
export function rollingBaseline(series: number[], window = 30) {
  return movingAverage(series, window);
}

// Compute days-until-stockout given current stock and forecasted daily demand
export function daysUntilStockout(stock: number, dailyForecast: number, leadTimeDays: number) {
  if (dailyForecast <= 0) return Infinity;
  return Math.floor(stock / dailyForecast) - leadTimeDays;
}