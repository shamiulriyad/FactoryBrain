# Camera AI — browser webcam + OpenAI vision

A judge or operator opens BunonBrain in a desktop browser, starts their webcam on a camera page, and
sees OpenAI's analysis of what the camera shows: people, activity, helmet, safety vest and possible
safety incidents, with approximate boxes. Confirmed changes become monitoring events and alerts.
Nothing is installed on the viewer's computer and no detection model is hosted: every image
understanding call goes to the OpenAI API from the BunonBrain server.

## Flow

```
Browser                               BunonBrain API (.NET)                               OpenAI
getUserMedia → <video> (30 fps, stays local)
every interval (default 1 s), only when the previous answer arrived:
  canvas → JPEG ≤640 px, q≈0.7 ──POST /api/monitoring/sessions/{id}/frames──▶ checks (JWT, owner, JPEG, interval,
                                       one frame in flight, concurrency, daily cap)
                                       ──Chat Completions, image input, strict JSON schema──▶ gpt-5.4-mini
                                       ◀── structured answer + token usage ─────────────────
                                       validate answer → temporal rules → events
                                       → monitoring pipeline (DB + alerts + SignalR)
  ◀── analysis, events, session stats ─
  boxes drawn over the preview; events also arrive over /hubs/monitoring
```

* The OpenAI key never reaches the browser: `CAMERA_AI_OPENAI_API_KEY` on the API, or the OpenAI key
  saved under Settings → AI. The browser only holds the user's own login token.
* Frames are analysed in memory and never stored. Only sampled frames leave the browser.
* **Latest frame wins.** The browser captures the current frame only when it is about to send it, and
  sends the next one only after the answer arrives. The server refuses a second frame while one is in
  flight (409) or before the interval (429 + Retry-After), so no queue can build up.

## Using it

1. A camera administrator registers a camera (type *Webcam*) and sets its video source to **Local
   webcam** (Cameras & Devices → camera → Change source).
2. Anyone who may view that camera opens **Cameras & Devices** → *Live AI camera* → the camera (or
   the camera's Monitoring page), chooses the webcam and the sampling interval, and presses **Start
   camera**. The browser asks for camera permission.
3. The page shows three separate blocks:
   * **AI estimate (latest frame).** What the model said about one still frame. It can be wrong.
   * **Confirmed over several frames.** What consecutive frames agreed on. This is what raises events
     and alerts. It is still AI-derived, so check in person.
   * **Measured.** Facts: frames sent, analysed, failed and skipped, round trip, OpenAI latency, JPEG
     size, tokens, estimated cost and today's usage.
4. **Stop camera** releases the webcam and ends the session. Closing the tab ends it too (a keepalive
   stop request). If that is lost, the server reports the camera OFFLINE after 15 s and ends the
   session after 2 minutes.

The browser needs HTTPS (or localhost) for the camera. Render serves HTTPS.

## API (additive; existing endpoints unchanged)

| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/api/monitoring/sessions` | monitoring.view or cameras.view | configured?, model, limits, today's usage, sessions in scope |
| POST | `/api/monitoring/sessions` | cameras.view or cameras.manage | `{cameraId, intervalMs?}` → 201. Camera in the caller's scope, Active, source LocalWebcam |
| GET | `/api/monitoring/sessions/{id}` | monitoring.view or cameras.view | session status, confirmed state, stats, last analysis, recent events |
| POST | `/api/monitoring/sessions/{id}/frames` | cameras.view or cameras.manage | raw `image/jpeg` body, owner only → 200 `{outcome, analysis \| error, events, session}` |
| POST | `/api/monitoring/sessions/{id}/stop` | cameras.view or cameras.manage | owner, or a camera manager of the factory; idempotent |

Errors are ProblemDetails with a stable `code`:

| Code | Status | Meaning |
|---|---|---|
| `ai_not_configured` | 503 | Camera AI is not set up on the server |
| `camera_not_found` | 404 | No such camera in the caller's scope |
| `camera_not_active` | 409 | The camera is inactive |
| `camera_not_browser_webcam` | 409 | The camera's source is not Local webcam |
| `camera_session_active` | 409 | Another user is already analysing this camera |
| `too_many_sessions` | 503 | The session limit is reached |
| `session_not_found` | 404 | The session has ended or does not exist |
| `session_not_yours` | 403 | Frames from someone other than the session's owner |
| `session_expired` | 409 | The session reached its maximum length |
| `frame_type_invalid`, `frame_not_jpeg`, `frame_size_invalid` | 400 | The frame is not a usable JPEG |
| `frame_too_large` | 413 | The frame is over the size limit |
| `analysis_in_progress` | 409 | A frame is still being analysed |
| `frame_too_soon` | 429 | Sent before the interval |
| `daily_limit_reached` | 429 | The daily call limit is used up |
| `ai_busy` | 503 | Concurrent OpenAI calls are at their cap |

A per-user rate-limit policy (`camera-ai`, 180 requests/min) sits in front of all of these.

## OpenAI call

* Model `gpt-5.4-mini` (`CAMERA_AI_MODEL`), Chat Completions.
* The image is sent as a base64 data URL, `detail` `low` (`CAMERA_AI_IMAGE_DETAIL`).
* Output uses **Structured Outputs**: `response_format: json_schema, strict: true`.
* `reasoning_effort` is not sent by default (the model's default, `none`); set `CAMERA_AI_REASONING_EFFORT` to change it.
* Time-out: 20 s per attempt. Up to 2 attempts, retrying only on time-outs, 429 rate limits and 5xx.
  401/403, quota exhaustion, 400 and model-not-found fail at once with a stable code
  (`unauthorized`, `quota_exceeded`, `provider_error`…).
* Refusals and cut-off answers are failures. Their tokens are still counted.
* Every call is written to `ai_api_usage` (purpose `camera.vision`): outcome, attempts, duration, input
  and output tokens, factory, user and session. It never stores the image, prompt or key. The daily
  limit is counted from these rows, so it survives restarts.

### The answer contract

| Field | Values |
|---|---|
| `person_presence` | present, absent, unclear |
| `person_count` | approximate integer, or null |
| `people[]` | per person: activity, helmet, safety_vest, approximate box (fractions of the frame) or null |
| `helmet`, `safety_vest` | **present / missing / unclear / unknown** (unknown = not visible) |
| `scene_activity` | the platform's activity labels (STANDING, SITTING, WALKING, BENDING, REACHING, SEWING_LIKE_MOTION, HANDLING_MATERIAL, OPERATING_MACHINE, PICKING_ITEM) or UNKNOWN |
| `safety_incident` | status none, possible or unclear; type FALL, PERSON_ON_GROUND, FIRE_OR_SMOKE, BLOCKED_PATH, UNSAFE_MACHINE_USE or OTHER |
| `image_quality` | good, poor, unusable |

No numeric confidence is asked for or invented. The model's scores are not calibrated, so events carry
no confidence value. The server validates every answer:
* A wrong type or a label outside the enums rejects the answer (`invalid_output`).
* Boxes are clamped into the frame; pixel-valued boxes are dropped.
* Contradictions are corrected and listed under "Checked by the server". For example, "absent" with
  people listed becomes "unclear".
* An unusable image proves nothing either way.

## Events (temporal confirmation)

A single frame never raises an event. Each state is reported once, when it changes:

| Event | When |
|---|---|
| `PERSON_DETECTED` | 2 consecutive "present" frames |
| `PERSON_LOST` | 3 consecutive "absent" frames, or the session ends while a person is confirmed |
| `ACTIVITY_CHANGED` (new) | a new scene activity (not UNKNOWN) holds for 3 frames while a person is confirmed |
| `PPE_VIOLATION` (new) | a required item (`CAMERA_AI_PPE_REQUIRED`, default helmet + vest) is "missing" in 3 consecutive frames. Unclear or unknown breaks the streak. Raised once per episode; the episode ends after 3 "present" frames or when the person is lost. Raises a **PPE_VIOLATION** alert (MEDIUM, one open alert per camera). |
| `DETECTION_UNAVAILABLE` (new) | 3 failed analyses in a row. Raises a **MODEL_UNAVAILABLE** alert. The next success sends `MODEL_STATUS_CHANGED` READY, which annotates the alert's recovery. |
| CAMERA_OFFLINE | `CAMERA_STATUS_CHANGED` with `toStatus` OFFLINE after 15 s without frames (the existing **CAMERA_OFFLINE** alert); ONLINE when frames resume |

These are stored by the existing monitoring pipeline with producer **`CAMERA_AI`** (source REAL,
never a worker identity). Each event's payload carries `source: openai-vision`, the session id,
`estimate: true` and a note. Events flow to the timeline, the alerts and SignalR like every other
producer. PERSON_* events use a session-scoped id (`ai-<12 hex>`) meaning "someone was in view during
this session", never a person. Other producers can't send the three new types
(`event_type_reserved_for_camera_ai`).

**Database change.** Migration `AddCameraAiEventTypes` only widens the check constraints on
`monitoring_events`, to allow the 3 event types and the producer `CAMERA_AI`, and adds
`CK_monitoring_events_camera_ai_anonymous`. There are no new tables. Usage reuses `ai_api_usage`;
sessions live in API memory. The migration is applied when the API starts.

## Settings (API environment)

| Variable | Default | |
|---|---|---|
| `CAMERA_AI_OPENAI_API_KEY` | — | OpenAI key for camera AI. If empty, the Settings → AI key is used when its provider is OpenAI |
| `CAMERA_AI_MODEL` | `gpt-5.4-mini` | |
| `CAMERA_AI_ENABLED` | `true` | |
| `CAMERA_AI_INTERVAL_MS` / `CAMERA_AI_MIN_INTERVAL_MS` | 1000 / 1000 | default and fastest sampling (the page offers 1, 2, 5 or 10 s) |
| `CAMERA_AI_IMAGE_DETAIL` | `low` | low, high or auto |
| `CAMERA_AI_REASONING_EFFORT` | (not sent) | e.g. `low` |
| `CAMERA_AI_MAX_OUTPUT_TOKENS` | 1500 | |
| `CAMERA_AI_TIMEOUT_SECONDS` / `CAMERA_AI_MAX_ATTEMPTS` | 20 / 2 | |
| `CAMERA_AI_MAX_FRAME_KB` | 600 | |
| `CAMERA_AI_MAX_SESSIONS` / `CAMERA_AI_MAX_CONCURRENT` | 10 / 4 | |
| `CAMERA_AI_DAILY_CALL_LIMIT` | 5000 | OpenAI calls per UTC day, all cameras |
| `CAMERA_AI_MAX_SESSION_MINUTES` | 30 | |
| `CAMERA_AI_OFFLINE_AFTER_SECONDS` / `CAMERA_AI_IDLE_TIMEOUT_SECONDS` | 15 / 120 | |
| `CAMERA_AI_PPE_REQUIRED` | `helmet,vest` | |
| `CAMERA_AI_PRICE_INPUT_PER_1M_USD` / `..._OUTPUT_...` | 0.75 / 4.50 for gpt-5.4-mini | used only to show cost estimates |

## Cost and speed (measured with real OpenAI calls, 2026-11-18)

gpt-5.4-mini costs $0.75 per 1M input tokens and $4.50 per 1M output tokens (OpenAI, checked
2026-11-18). A real test sent 9 browser-like frames (640 px JPEG, quality 70) through the
production client:

| Frame | Input / output tokens | OpenAI latency | Cost |
|---|---|---|---|
| 640×360, two people | 1,011 / 155 | 4.1 s (first call) | $0.0015 |
| 640×853, four people beside a bus | 1,371 / 240 | 3.3 s | $0.0021 |
| 640×261, nobody | 939 / 66 | 1.4 s | $0.0010 |
| 6-frame session (640×360 / 640×261) | avg 975 / 105 | avg 2.1 s | avg $0.0012 |

* **About $0.0012 per analysed 640×360 frame.** Bigger frames and more people cost more.
* **OpenAI answers in about 1.4–2.5 s** (the first call is slower). Only one frame is ever in flight
  (latest frame wins), so with "1 frame / second" selected the real rate is about **0.4–0.5 analysed
  frames per second**. The page shows the actual rate.

| Sampling chosen | Real analysed rate | Per camera-hour |
|---|---|---|
| 1 frame / s | ≈ 0.45 / s | ≈ $2 |
| 1 frame / 2 s | ≈ 0.4 / s | ≈ $1.8 |
| 1 frame / 5 s | 0.2 / s | ≈ $0.9 |

The default daily cap (5,000 calls) limits the worst case to about $6–10 per day. Every call's real
token count is in `ai_api_usage` and on the page.

The model's answers vary from call to call on the same frame: the activity was REACHING once,
STANDING once and UNKNOWN in other calls, and the helmet was "unknown" where heads were cropped. This
variation is why events wait for several agreeing frames.

## Limits and honesty

* These are AI estimates from still frames. They can miss people, misjudge PPE (a cap is not a hard
  hat) and mislabel activity. Boxes are approximate. Events wait for several agreeing frames, and
  alerts tell people to check in person.
* The page shows a frame's result about 1–3 s after capture (OpenAI latency), so boxes lag movement.
* Nothing identifies anyone. The prompt forbids identity guesses and the pipeline refuses worker ids
  from this producer.
* Sessions live in API memory. A restart (for example the Render free plan's sleep) ends them; the
  page then says the session ended and Start camera begins a new one.
* One live session per camera. The same user starting again replaces their own.
