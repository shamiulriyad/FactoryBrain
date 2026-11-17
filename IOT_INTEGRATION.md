# Machine monitoring, production sensors and IoT integration

How BunonBrain receives data from machine sensors, production counters,
ESP32s and industrial gateways, derives each machine's state, counts
production, raises machine alerts and shows everything in the central
monitoring dashboard — first with simulated devices, later with real
hardware, through the same contract.

> Machine data is machine data. A RUNNING machine doesn't prove anyone is
> operating it; a production count is machine output, not an individual's
> output; machine running time is never worker working time. Attendance,
> predicted activity and verified workstation identity stay separate records.

## 1. Architecture

```text
Physical machine / sensor
        │  current clamp, run/stop contact, piece sensor, temperature, vibration
ESP32 / industrial gateway (Modbus, OPC UA, PLC…)
        │  JSON over MQTT (preferred)          JSON over HTTPS (device key or integration account)
MQTT broker (Mosquitto, EMQX…) ───────► MqttBridgeService ─┐
                                                           ├─► IotIngestionService ── same parser, same rules for every transport
POST /api/iot/device/… · /api/iot/ingest/… ────────────────┤      │ resolve + check the device in the registry, transport, simulation,
IoT simulator (simulated devices only) ────────────────────┘      │ rate limit, timestamp
                                                                  ├─ heartbeat / status ─► existing device pipeline (health, device events)
                                                                  ├─ telemetry ──► MachineStateProcessor ─► machine_telemetry (raw)
                                                                  │                    machine_states (derived) + machine_state_transitions
                                                                  └─ production ─► ProductionCounterProcessor ─► production_counter_events
                                                                                       + production_counter_snapshots
MachineMonitorService (every 30 s): device health → OFFLINE / UNKNOWN, timed alert rules, telemetry retention
        │
MACHINE_STATE_CHANGED / MACHINE_CONDITION monitoring events ─► existing alert rules and lifecycle (machine alerts)
        │
SignalR /hubs/monitoring: machineState, productionChanged, monitoringEvent, monitoringAlert
        ▼
Central monitoring → Machines (overview, production, device health, history, alerts, simulator) · machine detail · monitoring room
```

A modular monolith, following the existing layers:

| Layer | IoT code |
|---|---|
| Domain | `Entities/Iot.cs` |
| Application (pure, unit-tested) | `Iot/IotCatalog.cs` (vocabulary), `Iot/IotMessages.cs` (topics + payload parser), `Iot/MachineStateEngine.cs`, `Iot/ProductionCounting.cs` (counting + shift calendar), `Iot/IotMetrics.cs` (durations, device keys, rate limiter), `Iot/IotOptions.cs`, `Dtos/Iot`, `Abstractions/Iot.cs` |
| Infrastructure | `Iot/IotIngestionService.cs` (pipeline), `Iot/IotProcessors.cs` (state + counting), `Iot/MachineMonitorService.cs` (+ worker), `Iot/IotQueryService.cs` (views + reports), `Iot/IotConfigService.cs` (config, keys, rules, maintenance, corrections), `Iot/IotSimulatorService.cs` (+ runner), `Iot/MqttBridge.cs` (MQTTnet client, adapter), `Persistence/Configurations/IotConfiguration.cs` |
| API | `Controllers/IotControllers.cs`, `Realtime/SignalRIotPublisher.cs` |

**Reused, not duplicated:** the device registry (devices, heartbeats, device
events, health monitor, device simulator), machines and workstations,
production lines, factory shifts and time zone, monitoring events and alerts
(and their acknowledge / escalate / resolve workflow), the monitoring SignalR
hub, RBAC and the audit log.

**New tables** (migration `AddIotMachineMonitoring`): `device_iot_configurations`,
`machine_monitoring_profiles`, `machine_states`, `machine_state_transitions`,
`machine_telemetry`, `production_counter_events`, `production_counter_snapshots`,
`production_corrections`; `MachineId` on `monitoring_events` and `monitoring_alerts`.

## 2. Devices and machines

1. **Machine** — Workstations → machines: code, type, the workstation it stands at (which gives the line).
2. **Device** — Cameras & devices → register: category *Machine sensor*, *Production counter*, *ESP32 gateway* or *Industrial gateway*; real or simulated (fixed); placement = the machine (and its workstation / line). The registry checks the machine stands at that workstation.
3. **IoT connection** — Monitoring → Machines → Device health → *Configure*:

| Field | Values |
|---|---|
| Device type | SEWING_MACHINE_SENSOR, RUN_STOP_SENSOR, POWER_SENSOR, CUTTING_MACHINE_SENSOR, PRESSING_MACHINE_SENSOR (category Machine sensor) · PIECE_COUNTER (Production counter) · RFID_READER · ESP32 · INDUSTRIAL_GATEWAY · MQTT_DEVICE / GENERIC_SENSOR |
| Protocol | **MQTT** (preferred), **HTTP** (device key), SIMULATOR (simulated devices only) |
| Connects through | DIRECT, ESP32, GATEWAY (+ which gateway device) |
| MQTT client id | the device's broker account / client id (for the broker ACL; never a password) |
| Telemetry interval, settings | informational; non-secret key/values (GPIO pin, Modbus register…) |

Each device has: id and code, type, factory, line, workstation, machine,
protocol, connection status (Online / Offline / Unknown), last heartbeat,
firmware version, configuration and registration date (`GET /api/iot/devices`).

4. **Machine rules** — machine page → *Rules* (`PUT /api/iot/machines/{id}/profile`): counting rule, power thresholds, alert thresholds (empty = off).

## 3. MQTT broker

The backend is an MQTT **subscriber** with its own account. Every device has
its own broker account; the broker's ACL limits each device to its own topics
(example: `deploy/mosquitto/acl`).

```bash
docker compose -f deploy/mosquitto/docker-compose.yml up -d     # local Mosquitto for real-device testing
```

| Setting (env) | Default | |
|---|---|---|
| `Iot__Mqtt__Enabled` | `false` | Off: HTTP and the simulator still work |
| `Iot__Mqtt__Host` / `Port` | `localhost` / `1883` | 8883 with TLS |
| `Iot__Mqtt__UseTls` | `false` | Use TLS whenever the broker is not on the same host |
| `Iot__Mqtt__Username` / `Password` | — | The backend's broker account. Only from configuration; never logged or returned |
| `Iot__Mqtt__ClientId` | `bunonbrain-backend` | |
| `Iot__Mqtt__TopicPrefix` | empty | e.g. `bunonbrain` ⇒ `bunonbrain/factory/…` |
| `Iot__Mqtt__PersistentSession` | `true` | QoS 1 messages published while the backend is down are delivered afterwards |
| `Iot__Mqtt__StatusTopic` | `bunonbrain/backend/status` | Retained `online`; last will `offline` |
| `Iot__Mqtt__ReconnectMinSeconds` / `MaxSeconds` | 2 / 60 | Exponential backoff |
| `Iot__TelemetryRetentionDays` | 30 | Raw telemetry only |
| `Iot__MessagesPerSecondPerDevice` / `PerMinute` | 20 / 600 | Per-device rate limit |
| `Iot__MonitorIntervalSeconds` | 30 | Health / timed rules |

Status: Monitoring → Machines → Device health → *MQTT bridge* (`GET /api/iot/bridge`:
connected, broker host:port, counters, last error — never credentials).

## 4. Topics

```text
[prefix/]factory/{factory}/device/{device}/telemetry     machine telemetry
[prefix/]factory/{factory}/device/{device}/status        ONLINE / OFFLINE / ERROR (also the device's last will), or a heartbeat
[prefix/]factory/{factory}/device/{device}/heartbeat     heartbeat
[prefix/]factory/{factory}/machine/{machine}/production  production counts (the payload names the counter device)
```

`{factory}`, `{device}` and `{machine}` are ids or codes. **They are only
lookup keys**: the device must be registered in that factory, configured for
MQTT and not disabled; a counter must be bound to that machine; a factory or
device id in the payload that disagrees with the topic is refused. QoS 1.

## 5. Message schema (`bunonbrain.iot.v1`)

Common fields: `messageId` (**required**, unique per device, 1–100 characters
`A–Z a–z 0–9 . _ : / -`; resend the same id when retrying), `occurredAt`
(UTC ISO 8601; at most 2 min ahead, 7 days back; default: received time),
optional `schema`, `type` (defaults from the topic / route), `firmwareVersion`.
At most 8 KB; unknown fields are ignored (and listed in the response notes).

**Telemetry** — at least one of `state`, a reading, `errorCode` or `metrics`:

```json
{
  "schema": "bunonbrain.iot.v1",
  "messageId": "t-3f9a21-1731657600-1042",
  "occurredAt": "2026-11-15T08:00:05Z",
  "state": "RUNNING",
  "powerWatts": 312.5,
  "currentAmps": 1.36,
  "temperatureC": 44.0,
  "vibrationMmS": 3.1,
  "metrics": { "rpm": 3500 }
}
```

`state`: RUNNING, STOPPED, IDLE, ERROR, MAINTENANCE (OFFLINE and UNKNOWN are
decided by the server). A power-only sensor omits `state`. An error:
`{"messageId":"t-…","errorCode":"THREAD_BREAK"}`.

**Status / last will** — `{"messageId":"s-…","status":"OFFLINE","message":"Connection lost (last will)"}`

**Heartbeat** — `{"type":"heartbeat","messageId":"h-…","firmwareVersion":"1.0.0","metrics":{"rssi":-61,"uptimeSeconds":3600}}`

**Production** — one of three forms:

```json
{ "messageId": "p-…", "deviceId": "PC-L1-T025", "counter": { "mode": "INCREMENT", "value": 1 } }
{ "messageId": "p-…", "deviceId": "PC-L1-T025", "counter": { "mode": "ABSOLUTE", "value": 15230 } }
{ "messageId": "p-…", "deviceId": "PC-L1-T025", "counter": { "mode": "RESET", "value": 0 } }
```

(`"count": 2` = INCREMENT 2; `"counterValue": 15230` = ABSOLUTE.) Increments are 1–10000.

**HTTP** — the same JSON:

| Route | Authentication | Body |
|---|---|---|
| `POST /api/iot/ingest/{telemetry\|status\|heartbeat\|production}` | integration account with `devices.manage` (gateways) | + `factoryId`, `deviceId` |
| `POST /api/iot/device/{telemetry\|status\|heartbeat\|production}` | headers `X-Device-Id` + `X-Device-Key` | `deviceId` optional (must be the key's device) |

Responses: `201` Accepted, `200` Duplicate, `400` invalid (code + reason),
`401` bad device key, `403` / `404` scope, `409` device state (simulated,
disabled, not MQTT, rate limited…). Body: `outcome`, `machineState`,
`countStatus`, `pieces`, `notes`. The full contract: `GET /api/iot/contract`.

## 6. Machine state rules

| Evidence | State |
|---|---|
| `errorCode` in telemetry | ERROR |
| `state` in telemetry | that state |
| power ≥ running threshold / ≥ idle threshold / below | RUNNING / IDLE / STOPPED (thresholds per machine; none = power says nothing) |
| no machine sensor reachable (device offline: status OFFLINE, last will, or heartbeats stopped) | **OFFLINE** — never STOPPED |
| sensor back online, or online but no telemetry for `StaleAfterSeconds` (default 900) | UNKNOWN |
| a person starts maintenance | MAINTENANCE (telemetry is stored but can't change it; ending ⇒ UNKNOWN) |

* Raw telemetry (`machine_telemetry`) is kept apart from the derived state (`machine_states`, `machine_state_transitions`).
* Out of order: a message older than the newest applied one is stored (outcome LATE) but never moves the state backwards.
* Simulated data never moves the state of a machine that has a real sensor (outcome SOURCE_MISMATCH).
* Durations (running, stopped, idle, error, offline, unknown, maintenance) come from consecutive transitions.

## 7. Production counter rules

| Message | Counted |
|---|---|
| INCREMENT n | n pulses — also when late (counted in the hour and shift it happened in) |
| ABSOLUTE v (first) | BASELINE: nothing |
| ABSOLUTE v ≥ last | v − last |
| ABSOLUTE v older than the newest | OUT_OF_ORDER: nothing (already included) |
| ABSOLUTE v < last | counter restart detected: v counted, noted |
| ABSOLUTE jump > 50 000 | SUSPECT_JUMP: nothing; correct manually if real |
| RESET v | RESET: nothing (also `POST /api/iot/production/counter-reset`, audited) |

Pulses become pieces by the machine's **counting rule**: `PIECE` (one count
= one piece), `PULSES_PER_PIECE` (N pulses = one piece; the remainder carries
over), `CYCLE_NOT_PIECE` (machine cycles: recorded, never pieces). The rule
in force is copied onto each count.

Each count event stores device, machine, workstation, line, factory,
counter value / increment, occurred and received time, source (REAL /
SIMULATED), transport, idempotency key (device + messageId — the same message
over MQTT and HTTP is counted once), the factory shift and production date
(overnight shifts belong to the day they start). **Corrections**
(`POST /api/iot/production/corrections`, ± pieces with a reason) are separate,
audited rows; counts are never edited.

**Metrics** (`GET /api/iot/production`): total, by line / workstation /
machine / shift, hourly (ranges ≤ 3 days), daily, machine running time,
idle, downtime (stopped + error), pieces per hour and per running hour. One
source at a time (REAL default, or SIMULATED): never mixed. Camera activity
predictions are never used as production quantity.

## 8. Machine alerts

Through the existing monitoring events and alert lifecycle (acknowledge,
escalate, resolve, dismiss; one open alert per machine and type; recovery
notes; history):

| Alert | Raised by | Default |
|---|---|---|
| MACHINE_OFFLINE (medium) | state → OFFLINE | — |
| MACHINE_ERROR (high) | state → ERROR | — |
| MACHINE_UNEXPECTED_STOP (medium) | STOPPED for `StopAlertAfterSeconds` during a scheduled shift, not in a break | 600 s |
| MACHINE_LONG_IDLE (low) | IDLE for `LongIdleAlertSeconds` | 1200 s |
| MACHINE_ABNORMAL_TEMPERATURE (high) | temperature > limit (clears 2 °C below) | off |
| MACHINE_HIGH_VIBRATION (medium) | vibration > limit (clears at 90 %) | off |
| PRODUCTION_COUNTER_DISCONNECTED (medium) | counter offline, or RUNNING without counts for `CounterSilenceAlertSeconds` | 900 s |
| MACHINE_UNUSUAL_TRANSITION (low) | ≥ N state changes in 5 min | 12 |

Each alert has its id, factory, machine, device, workstation, line,
severity, type, time, source (REAL / SIMULATED) and evidence. Machine events
are derived only: `POST /api/monitoring/events` refuses MACHINE_* types.

## 9. Dashboard

Monitoring → **Machines** (`/app/monitoring/machines`): counts (total,
running, idle, stopped, error, offline, pieces today, downtime today); tabs
*Machines* (live cards), *Production* (totals, by line / shift / workstation,
hourly / daily, per machine with time-in-state bars, corrections), *Device
health* (devices, health, protocol, last heartbeat, messages per hour, MQTT
bridge), *State history*, *Counter history*, *Machine alerts* (with the usual
actions) and *IoT simulator*. Filters: factory, department, floor, line,
workstation, machine, device, shift, date range, real / simulated. Machine
page (`/app/monitoring/machines/{id}`): current state and readings, today's
time by state and pieces, devices, alerts, state history, raw telemetry,
counter history, rules, maintenance, counter reset, and the workstation's
assignment and verified identity **beside** (never merged into) the machine
data. The monitoring room has a machine summary. Live over SignalR
(`machineState`, `productionChanged` to the factory's and line's SYSTEM
groups; the production ping carries no numbers).

## 10. Simulator

Monitoring → Machines → **IoT simulator** (`devices.simulate`):

1. *Set up simulated devices* on a placed machine without real devices
   (`POST /api/iot/simulator/setup`, needs `devices.manage`): a simulated
   sensor `M-…-SIM-S` and counter `M-…-SIM-C`, demo thresholds (running ≥ 150 W,
   idle ≥ 20 W, 70 °C, 12 mm/s).
2. Switch *Data* to **Simulated data**.
3. *Send a scenario* to a machine, the chosen workstation or line, or the
   factory: RUNNING, STOPPED, IDLE, ERROR, OFFLINE, ONLINE, HEARTBEAT, PIECES,
   COUNTER_RESET, TEMPERATURE, VIBRATION, DUPLICATE, DELAYED, INVALID. The
   result shows each topic, payload and outcome.
4. Or a *continuous simulation* (MIXED, BUSY, FAULTY; ≤ 2 h; ≤ 5 at once).

The simulator writes the JSON a device would publish, on its topic, and
passes the bytes through the same parser and pipeline; only simulated
devices; everything is SIMULATED and never added to real figures.

```bash
curl -X POST http://localhost:5000/api/iot/simulator/send -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"scenario":"PIECES","factoryId":"<factory id>","machineId":"<machine id>","count":5}'
```

## 11. ESP32 and gateways

* **ESP32** (`deploy/esp32/esp32_machine_sensor.ino`): CT clamp → power telemetry
  every 5 s, debounced piece sensor → increments, heartbeat every 30 s, last
  will OFFLINE, NTP UTC time, unique messageIds (chip id + boot + sequence).
  Prefer power-only telemetry and let the backend's thresholds decide the state.
* **Industrial gateway** (Modbus / OPC UA / PLC): register the gateway
  (category Industrial gateway) and each machine sensor / counter behind it
  (connection via GATEWAY, gateway = it). The gateway publishes for each
  device on that device's topic (or posts to `/api/iot/ingest/…` with an
  integration account), maps PLC registers to `state` / readings, sends
  ABSOLUTE counter values (the backend handles restarts and out-of-order
  readings), buffers while offline and resends with the original
  `messageId` and `occurredAt` (≤ 7 days).
* **HTTP device**: protocol HTTP → *Issue key* (shown once; stored hashed) →
  `X-Device-Id` / `X-Device-Key` on `/api/iot/device/{channel}`.

## 12. Security

* Devices authenticate: MQTT at the broker (one account per device, ACL to
  its own topics, TLS beyond the local network); HTTP with a per-device key
  (SHA-256 at rest, constant-time check, rotatable, revocable) or an
  integration account with `devices.manage`.
* Nothing in a topic or payload is trusted: factory, device and machine are
  looked up and must match the registry; simulated and real devices only
  accept their own transport; disabled devices are refused.
* Idempotency (device + messageId), timestamp limits, schema validation
  (8 KB, types, ranges), per-device rate limits (and a per-IP / device limit
  on the device-key route), invalid payloads recorded on the device's event log.
* Broker credentials only from configuration, never logged or returned.
* Factory isolation everywhere (API and SignalR groups); line-scoped users
  see their lines' machines.
* Audit: IoT configuration, device keys (prefix only), machine rules,
  maintenance, counter resets, production corrections.
* Raw telemetry is never updated (deleted only by retention); transitions,
  counts and corrections are append-only (database triggers).

**Access** (existing permissions): machine views `monitoring.view` /
`devices.view` / `production.view`; production counts `production.view` /
`monitoring.view`; IoT configuration, rules, maintenance, keys, setup
`devices.manage`; corrections `production.manage`; counter reset
`production.manage` or `devices.manage`; simulator `devices.simulate`.
Super Admin everything; Factory Admin everything in their factories; Factory
Manager overview, production and corrections; Production Supervisor their
lines' machines and production; Monitoring Operator machine status, alerts and
production counts (through monitoring.view); IT / Device Administrator devices, configuration, health and the
simulator (no production counts); HR and Workers: no machine data.

## 13. API

| Method | Path | Permission |
|---|---|---|
| POST | `/api/iot/ingest/{channel}` | `devices.manage` |
| POST | `/api/iot/device/{channel}` | device key |
| GET | `/api/iot/overview` · `/machines` · `/machines/{id}` · `/machines/{id}/telemetry` · `/state-history` | `monitoring.view` / `devices.view` / `production.view` |
| PUT | `/api/iot/machines/{id}/profile` | `devices.manage` |
| POST | `/api/iot/machines/{id}/maintenance` | `devices.manage` |
| GET | `/api/iot/production` · `/production/counts` · `/production/corrections` | `production.view` / `monitoring.view` |
| POST | `/api/iot/production/corrections` | `production.manage` |
| POST | `/api/iot/production/counter-reset` | `production.manage` / `devices.manage` |
| GET | `/api/iot/devices` · `/devices/{id}` · `/bridge` · `/contract` | `devices.view` / `monitoring.view` |
| PUT | `/api/iot/devices/{id}/config` | `devices.manage` |
| POST / DELETE | `/api/iot/devices/{id}/key` | `devices.manage` |
| POST | `/api/iot/monitor/run` | `devices.manage` |
| GET | `/api/iot/simulator` · `/simulator/runs` | `devices.simulate` |
| POST | `/api/iot/simulator/send` · `/simulator/runs` · `/simulator/runs/{id}/stop` | `devices.simulate` |
| POST | `/api/iot/simulator/setup` | `devices.manage` |

Existing endpoints used as before: device registration / placement
(`/api/devices`), device heartbeat and events (`/api/devices/{id}/heartbeat`,
`/events`), machines (`/api/machines`), alerts (`/api/monitoring/alerts`,
now filterable by `machineId`), events (`machineId` filter).

## 14. Hardware integration checklist

1. Machine registered and placed at its workstation; device registered (real), placed on the machine.
2. IoT connection: type, protocol MQTT (client id = broker username) or HTTP (issue a key).
3. Broker account + ACL for the device; TLS if off the local network.
4. Device clock on NTP, UTC timestamps; unique, stable messageIds; resend with the same id.
5. Machine rules: power thresholds measured on the real machine (idle vs sewing), counting rule (what one pulse means), alert limits from the manufacturer.
6. Heartbeat ≤ the device's heartbeat interval; offline window (`OfflineAfterSeconds`) longer than the longest normal pause.
7. Last will on the status topic (`status: OFFLINE`).
8. Check in Device health: Online, messages per hour, MQTT bridge counters; then the machine page: state, telemetry outcomes APPLIED, counts COUNTED.
9. Compare a shift's counter total with a hand count before relying on it; correct differences with a reason.

## 15. Troubleshooting

| Symptom | Check |
|---|---|
| MQTT bridge Disconnected | Host / port / TLS, backend credentials (`Iot__Mqtt__…`), broker logs; the last error is on the bridge panel |
| Messages arrive but nothing changes | Device protocol must be MQTT (`device_not_mqtt`); topic factory / device must match the registry; see the bridge's Rejected counter and the API log |
| `message_id_required` / `occurred_in_future` | Send a messageId; sync the device clock (UTC) |
| Machine stays UNKNOWN | Telemetry without state and no power thresholds; set the machine's rules, or send `state` |
| Machine OFFLINE but running | Its sensor's heartbeats stopped (Device health); a missing heartbeat is never read as stopped |
| Telemetry outcome LATE | Older than the newest applied message (buffered by a gateway): stored, state unchanged — expected |
| Counts missing or doubled | Counts are per device + messageId; check the counter history status (BASELINE, OUT_OF_ORDER, SUSPECT_JUMP) and the counting rule |
| `rate_limited` | The device sends more than `Iot__MessagesPerSecondPerDevice`; slow it down or batch increments |
| Simulated machine not listed | Switch *Data* to Simulated; set up simulated devices first |

## 16. Running it

```bash
# Backend (applies migrations at startup; Supabase connection from the repo-root .env)
cd BunonBrain.Backend/BunonBrain.Api && dotnet run
# Frontend
cd frontend && npm run dev
# Optional: local broker for real devices
docker compose -f deploy/mosquitto/docker-compose.yml up -d
# Tests (unit + in-process MQTT broker; DB tests need Docker)
cd BunonBrain.Backend && dotnet test BunonBrain.Api.Tests
```

Publish a test message to a local broker:

```bash
mosquitto_pub -h localhost -u S-L1-T025 -P '<device password>' -q 1 \
  -t factory/MAIN/device/S-L1-T025/telemetry \
  -m '{"messageId":"t-1","occurredAt":"2026-11-15T08:00:05Z","powerWatts":312.5}'
```

## 17. Limitations

* The MQTT bridge processes messages in arrival order on one consumer per
  API instance; several API instances need a shared subscription (MQTT 5
  `$share/…`) or one bridge instance.
* Rate limits and continuous simulations are in memory per instance;
  simulations end when the API restarts.
* Broker-side ACLs are the MQTT device authentication; the backend can't see
  which account published a message, so it checks the topic against the
  registry and protocol (configure the ACL).
* Hourly production buckets for ranges up to 3 days; reports up to 93 days.
* Telemetry is stored per message (retention 30 days by default); no
  down-sampled long-term telemetry yet.
* No OPC UA / Modbus driver in the backend: gateways translate to this contract.
* The database integration tests need Docker (Testcontainers).












