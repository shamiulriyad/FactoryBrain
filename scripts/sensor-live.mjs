// Posts one live sewing-machine reading to the API.
// The overview poll then shows this machine instead of inventing a tick.
//
//   node ./scripts/sensor-live.mjs
//   node ./scripts/sensor-live.mjs --entity M-101 --vibration 6.4 --temp 82
//
// API base: SENSOR_API_URL or http://localhost:5000

const base = process.env.SENSOR_API_URL || "http://localhost:5000";
const args = process.argv.slice(2);
function flag(name, fallback) {
  const i = args.indexOf(name);
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback;
}

const entity = flag("--entity", "M-101");
const line = flag("--line", "line-3");
const vibration = Number(flag("--vibration", "6.4"));
const temp = Number(flag("--temp", "82"));
const efficiency = Number(flag("--efficiency", "58"));

const body = {
  readings: [
    { source: "telemetry", entityId: entity, metric: "vibration_rms", value: vibration, unit: "mm/s" },
    { source: "telemetry", entityId: entity, metric: "temperature_c", value: temp, unit: "°C" },
    { source: "rfid", entityId: line, metric: "efficiency", value: efficiency, unit: "%" }
  ]
};

const res = await fetch(`${base}/api/sensors/live`, {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify(body)
});
const text = await res.text();
console.log(res.status, text);
if (!res.ok) process.exit(1);
