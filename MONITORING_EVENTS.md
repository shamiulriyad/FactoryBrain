# Monitoring events: the ingestion contract

How producers send events to BunonBrain's central monitoring, and how the
events reach people. Producers are gateways today and, in a later phase, the
Python (FastAPI) AI service. Nothing in this phase runs computer vision: the
contract exists so that service can plug in without changes here.

Permissions and roles are in [ACCESS_CONTROL.md](ACCESS_CONTROL.md) (section 4,
rule 14). The first real producer is the computer-vision service in
[computer-vision/](computer-vision/README.md) (YOLO + ByteTrack).

**Service-to-service sign-in.** A producer service sends `X-Service-Key:
<key>` instead of a person's token. The backend accepts the key in
`CV_SERVICE_KEY` (at least 32 characters, kept only as a hash) for the
factories in `CV_SERVICE_FACTORIES` (codes, or `*`). That principal has no role
and exactly one permission, `monitoring.events.ingest`; its events are stored
with producer `CV_SERVICE`, must be REAL, must concern an active camera, may
only be vision (including activity), camera-status or model-status events,
may only report the observable activities (section 2.1), and can never carry
a worker id. It alone may read `GET /api/integrations/cv/cameras[/{id}[/video]]`
(sources with decrypted stream credentials, active zones); people get 403
there whatever their permissions.

## 1. Sending an event

```
POST /api/monitoring/events
Authorization: Bearer <access token of an account with monitoring.events.ingest>
Content-Type: application/json
```

```json
{
  "eventType": "ZONE_ENTERED",
  "factoryId": "3f1c2a9e-0000-4000-8000-000000000001",
  "idempotencyKey": "cam-001/2026-11-14T08:15:02.120Z/t-42/zone-in",
  "occurredAt": "2026-11-14T08:15:02.120Z",
  "sourceType": "REAL",
  "cameraId": "…",
  "zoneId": "…",
  "trackId": "t-42",
  "confidence": 0.87,
  "modelName": "person-detector",
  "modelVersion": "1.4.0",
  "payload": { "bbox": { "x": 0.41, "y": 0.22, "w": 0.09, "h": 0.31 } }
}
```

| Field | Required | Notes |
|---|---|---|
| `eventType` | yes | See section 2. Case-insensitive; `-` and spaces are read as `_`. |
| `factoryId` | yes | The caller must reach this factory (factory-level role or Super Admin). |
| `idempotencyKey` | yes | 1–128 characters: letters, digits, `.` `_` `:` `/` `-`. One event per factory and key. |
| `occurredAt` | no | UTC, ISO 8601. Default: time received. At most 2 minutes ahead and 7 days old. |
| `sourceType` | no | `REAL` (the only value this endpoint accepts). Simulated events come from the simulator. |
| `cameraId`, `deviceId`, `zoneId`, `workstationId`, `lineId` | per type | All must belong to `factoryId`. A zone must belong to the camera; a workstation must be on the line. Missing line, workstation, floor and department are filled in from the zone, workstation, camera or device. |
| `trackId` | per type | Camera-local, temporary, 1–64 characters. **Not a worker identity.** Needs `cameraId`. |
| `workerId` + `identity` | no | Only with a verified identity, see section 3. |
| `confidence` | per type | 0–1. |
| `modelName`, `modelVersion` | no | Up to 80 / 40 characters. `MODEL_STATUS_CHANGED` needs a model name (or `payload.model`). |
| `payload` | no | A JSON object, up to 8 KB and 5 levels deep. No secrets (`password`, `token`, `apiKey`, …) and no identity fields (`workerId`, `employeeCode`, `faceEmbedding`, …). |

**Responses**

| Status | Meaning |
|---|---|
| `201 Created` | Stored. Body: `{ outcome: "Accepted", event, alert? }` (`alert` when the event raised or updated one). |
| `200 OK` | The key was seen before: `{ outcome: "Duplicate", event }` returns the stored event unchanged. Safe to retry. |
| `400` | Contract violation. `code` says what, e.g. `track_id_required`, `zone_not_on_camera`, `confidence_invalid`, `payload_invalid`, `worker_identity_unverified`. |
| `403` | No `monitoring.events.ingest`, or the factory is out of reach (`factory_out_of_scope`). |
| `409` | `idempotency_key_reused` (same key, different event type), `device_is_simulated` (a simulated device can't report REAL events). |

`GET /api/monitoring/contract` returns the same rules as data (types, required
fields, status values, limits).

## 2. Event types

| Type | Category | Needs | `payload` |
|---|---|---|---|
| `PERSON_DETECTED` | VISION | `cameraId` | e.g. `bbox` |
| `TRACK_UPDATED` | VISION | `cameraId`, `trackId` | e.g. `bbox` |
| `PERSON_LOST` | VISION | `cameraId`, `trackId` | |
| `ZONE_ENTERED`, `ZONE_EXITED` | VISION | `zoneId` (camera taken from it), `trackId` | |
| `ACTIVITY_CLASSIFIED` | VISION | `cameraId`, `trackId`, `confidence` | `activity` (catalog, section 2.1): a periodic confirmation of a track's predicted activity |
| `ACTIVITY_STATE_CHANGED` | VISION | `cameraId`, `trackId`, `confidence` | `activity` (not UNKNOWN), optional `previousActivity`, `observationStart` / `observationEnd`, `quality`, `scores` |
| `ACTIVITY_UNCERTAIN` | VISION | `cameraId`, `trackId` | `activity` = `UNKNOWN`, optional `previousActivity`, `reason`, `candidate`. Never carries a worker. |
| `GARMENT_ACTIVITY_CLASSIFIED` | VISION | `cameraId`, `trackId`, `confidence`, `modelName`, `modelVersion` | `activity` (garment label), `model`, `context` |
| `GARMENT_ACTIVITY_CHANGED` | VISION | `cameraId`, `trackId`, `confidence`, `modelName`, `modelVersion` | `activity` (not UNKNOWN), optional `previousActivity` |
| `GARMENT_ACTIVITY_UNCERTAIN` | VISION | `cameraId`, `trackId`, `modelName`, `modelVersion` | `activity` = `UNKNOWN`, `reason`, `candidate`. Never carries a worker. |
| `UNCERTAIN_DETECTION` | VISION | `cameraId`, `confidence` | e.g. `reason`. Never carries a worker. |
| `CAMERA_STATUS_CHANGED` | SYSTEM | `cameraId` | `toStatus`: ONLINE, OFFLINE, ACTIVE, INACTIVE, MAINTENANCE |
| `DEVICE_STATUS_CHANGED` | SYSTEM | `deviceId` | `toStatus`: ONLINE, OFFLINE, ERROR, ACTIVE, MAINTENANCE, DISABLED |
| `MODEL_STATUS_CHANGED` | SYSTEM | `modelName` | `toStatus`: LOADING, READY, DEGRADED, ERROR, OFFLINE |
| `MACHINE_STATE_CHANGED` | SYSTEM | `machineId` (IoT pipeline only) | `toStatus` / `fromStatus`: RUNNING, STOPPED, IDLE, MAINTENANCE, ERROR, OFFLINE, UNKNOWN; `reason`, `trigger` |
| `MACHINE_CONDITION` | SYSTEM | `machineId` (IoT pipeline only) | `toStatus`: ACTIVE or CLEARED; `condition`: UNEXPECTED_STOP, LONG_IDLE, ABNORMAL_TEMPERATURE, HIGH_VIBRATION, COUNTER_DISCONNECTED, UNUSUAL_TRANSITION |

Machine events are derived by the IoT pipeline from machine sensor data
(producer `IOT_PIPELINE`, REAL or SIMULATED like their devices); the ingestion
API refuses them (`machine_events_are_derived`). They never carry a worker.
See [IOT_INTEGRATION.md](IOT_INTEGRATION.md).

VISION events are visible with `monitoring.view` only. SYSTEM events are also
visible to IT / Device Administrators (`devices.view`).

### 2.1 Activity events

An activity is a **prediction** about a temporary track: its posture or
movement over a few seconds, with a confidence. It is not a worker identity,
not verified work and not a productivity or performance measure, and no
alert rule acts on it.

| `payload.activity` | Who may send it |
|---|---|
| `STANDING`, `SITTING`, `WALKING`, `BENDING`, `REACHING`, `UNKNOWN` (observable) | every producer, including the computer-vision service |
| `SEWING_LIKE_MOTION`, `HANDLING_MATERIAL`, `OPERATING_MACHINE`, `PICKING_ITEM` (reserved) | producers with a trained, validated model; **refused from the computer-vision service** (`activity_not_supported_by_service`) |

Anything else is refused (`activity_invalid`), and so is a previous activity
outside the catalog (`previous_activity_invalid`). `ACTIVITY_UNCERTAIN` must
carry `UNKNOWN` (`activity_must_be_unknown`); a change to UNKNOWN is never an
`ACTIVITY_STATE_CHANGED` (`activity_state_unknown`). `ACTIVITY_UNCERTAIN`
can't carry a worker (`uncertain_activity_cannot_identify`; the database
enforces it too). Values are upper-cased on the way in. Summaries read
"Predicted activity changed to WALKING from STANDING on CAM-001 (track …)".

### 2.2 Garment activity events

A separate family from the generic posture activity, predicted by a trained
model (computer-vision/docs/GARMENT_ACTIVITIES.md). `payload.activity` is
any upper-snake-case label of 2–40 characters (the classes live in the CV
service's configurable registry: SEWING, FABRIC_HANDLING, CUTTING, IRONING,
MATERIAL_PICKING, MACHINE_OPERATION, QUALITY_INSPECTION, PACKING, WALKING,
STANDING, SITTING, UNKNOWN, plus factory-specific ones). The event must name
its model (`model_version_required` otherwise); `GARMENT_ACTIVITY_UNCERTAIN`
must carry UNKNOWN and can never name a worker (database-enforced);
`GARMENT_ACTIVITY_CHANGED` never carries UNKNOWN. `payload.model` holds the
dataset version and evaluation status; `payload.context` (zone,
workstation, line) is context only, never evidence of the activity.
`GET /api/monitoring/activity?kind=garment` returns the garment picture
(same shape, plus `kind` and `models`: name, version, event count,
evaluation status). The timeline's `activity` filter matches both families.

`GET /api/monitoring/activity?factoryId&lineId&cameraId&activity&sourceType&uncertainOnly&from&to&activeSeconds`
(`monitoring.view`) returns the activity picture: tracks whose latest
activity event is recent and who weren't lost (`current`), each track's
latest predicted activity, confidence, since when, reason / best guess when
uncertain and verified worker (RFID only, never from a camera); observed
seconds and prediction counts per activity by camera and by line (time
between a track's consecutive activity events, at most 2 minutes each); the
latest 50 transitions (state changes and uncertain periods). Default range:
the last hour; at most 20 000 events are used (`truncated`). `GET
/api/monitoring/events?activity=WALKING` filters the timeline.

Device connection, error and status changes recorded by the device module are
mirrored here automatically (`producer: DEVICE_PIPELINE`), and a camera's
health device going online / offline also produces `CAMERA_STATUS_CHANGED`.
A producer doesn't need to send those.

## 3. Identity rules

- A `trackId` is a number a camera's tracker gave a moving person. It is
  temporary, local to that camera, and never a worker id.
- `workerId` is accepted only as
  `{ "workerId": "…", "identity": { "method": "RFID_BADGE", "cardUid": "04A1B2C3D4" } }`
  (or just the `identity`: the server looks the worker up). The card must be
  an active card of that worker in the event's factory.
- Identification by appearance (`FACE_RECOGNITION`, `VISUAL_SIMILARITY`,
  `RE_ID`, …) is refused (`identity_method_not_accepted`), whatever the
  confidence.
- `UNCERTAIN_DETECTION` never names anyone (`uncertain_detection_cannot_identify`),
  and simulated events never do. The database enforces both.
- Linking a track to a worker is the backend's job, not the producer's: the
  identity association service combines an RFID read at a workstation-level
  reader, the worker's active assignment and the track's `ZONE_ENTERED` /
  `ZONE_EXITED` / `PERSON_LOST` events in that workstation's camera zone, and
  marks the track VERIFIED only if all agree (otherwise AMBIGUOUS / UNVERIFIED /
  MANUAL_REVIEW). A `PERSON_LOST` ends the track's identity. Changes go out as
  the SignalR message `identityAssociation` to the VISION groups. See
  [IDENTITY_ASSOCIATION.md](IDENTITY_ASSOCIATION.md).

## 4. What happens to an event

1. Validated, then stored once (append-only: never updated or deleted).
2. Alert rules run in the same transaction:

   | Situation | Alert | Severity |
   |---|---|---|
   | Camera status → OFFLINE | `CAMERA_OFFLINE` | HIGH |
   | Device status → OFFLINE / ERROR | `DEVICE_OFFLINE` / `DEVICE_ERROR` | MEDIUM |
   | Model status → ERROR / OFFLINE / DEGRADED | `MODEL_UNAVAILABLE` | MEDIUM / LOW |
   | `UNCERTAIN_DETECTION` | `UNCERTAIN_DETECTION_REVIEW` | LOW |
   | `ZONE_ENTERED` a zone of type Restricted | `RESTRICTED_ZONE_ENTRY` | HIGH |
   | Machine → OFFLINE / ERROR | `MACHINE_OFFLINE` / `MACHINE_ERROR` | MEDIUM / HIGH |
   | Machine condition ACTIVE | `MACHINE_UNEXPECTED_STOP`, `MACHINE_LONG_IDLE`, `MACHINE_ABNORMAL_TEMPERATURE`, `MACHINE_HIGH_VIBRATION`, `PRODUCTION_COUNTER_DISCONNECTED`, `MACHINE_UNUSUAL_TRANSITION` | LOW – HIGH |

   The same situation again updates the open alert (occurrence count). A
   return to normal (ONLINE / READY) is noted on the open alert, which a person
   still resolves. No rule judges a person's productivity, and no alert
   changes pay, attendance or discipline.
3. The event and any alert change are pushed over SignalR to the people allowed
   to see them (section 5).

People then acknowledge, escalate, resolve or dismiss alerts, and record
reviews of events (`CONFIRMED`, `FALSE_POSITIVE`, `STILL_UNCERTAIN`,
`NEEDS_FOLLOW_UP`) beside the unchanged event.

## 5. Real-time updates

Hub: `/hubs/monitoring` (SignalR, JSON protocol; WebSockets with fallbacks).
Send the access token as `access_token` in the query string for WebSockets, or
the `Authorization` header otherwise. Connecting needs `monitoring.view` or
`devices.view`.

1. Connect, then call `Subscribe(factoryId)` (or `Subscribe(null)` for all of
   your factories). It returns what the connection now receives:
   `{ allFactories, factories, lines, categories }`. A factory you can't reach
   is refused.
2. Messages:
   - `monitoringEvent`: an event (same shape as the REST API).
   - `monitoringAlert`: `{ change: "created" | "repeated" | "recovered" | "acknowledged" | "escalated" | "resolved" | "dismissed", alert }`.
     The pushed alert's `can` flags are all false; fetch the alert to see
     what you may do.
   - `cameraStatus`, `deviceStatus`: `{ id, code, status, sourceType, occurredAt, eventId }`.
   - `machineState`: `{ change: "state" | "condition", machine }` (SYSTEM groups).
   - `productionChanged`: `{ factoryId, lineId, machineId, sourceType, at }` — no numbers; reload through `/api/iot/production`.
3. After a reconnect, call `Subscribe` again (a new connection has no groups)
   and refetch to fill the gap. The server closes the connection when the
   access token expires; reconnect with a fresh token.

Groups: a factory's VISION group and SYSTEM group, a platform group per
category (Super Admin following all factories), and one group per production
line (Production Supervisors). Nothing is broadcast to every connection.

## 6. Running the monitoring simulator

The simulator produces SIMULATED events through the same pipeline. It needs at
least one active camera in the factory (zone events need zones; device status
needs a simulated device).

1. Sign in as a Monitoring Operator (or Factory / Super Admin).
2. Open **Monitoring room → Simulator** (`/app/monitoring/simulator`).
3. Pick the cameras, events per minute (1–120), how long it may run (1–120
   minutes) and the event types; press **Start simulation**. It stops by itself
   at the end, or press **Stop simulation**.
4. Or send one event with **Trigger one demo event** (e.g. Uncertain
   detection, Zone entered on a Restricted zone, Camera status OFFLINE).

API: `POST /api/monitoring/simulator/start`, `POST /api/monitoring/simulator/{runId}/stop`,
`POST /api/monitoring/simulator/events`.

## 7. Testing real-time updates with two sessions

1. Make sure the factory has an active camera (Cameras & devices → Register
   camera; choose a SIMULATED health device so connection changes can be
   simulated too) and, for zone events, a zone.
2. **Session A** (normal browser window): sign in as a Monitoring Operator and
   open **Monitoring room**. Wait for the green **Live** indicator.
3. **Session B** (a private window or another browser, so the sessions don't
   share a login): sign in as another user with `monitoring.simulate`, e.g. a
   second operator or the Factory Admin, and open **Monitoring room → Simulator**.
4. In session B, start a simulation or trigger an event. Session A's live feed
   shows it within a second, labelled SIMULATED; the counters update.
5. Trigger **Camera status → OFFLINE** in B. A shows a `CAMERA_OFFLINE` alert;
   acknowledge it in A, then open **Alerts** in B: the status changed without
   reloading. The alert's history lists both actions with who did them.
6. Sign in as a Production Supervisor of another line, or as an operator of
   another factory: those events never arrive there.
