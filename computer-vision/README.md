# BunonBrain computer-vision service

A separate Python service that detects **people** in camera video with a
pretrained **YOLO** model, follows them with **ByteTrack**, works out which
configured **camera zone** each person stands in, optionally predicts each
tracked person's **basic physical activity** (pose estimation + temporal
classification: standing, sitting, walking, bending, reaching, or unknown)
and, with a trained model, **garment-specific activities** (sewing, cutting,
packing…, or unknown), and sends structured events to the BunonBrain ASP.NET
Core backend, which shows them live in the Central Monitoring Room.

It does **not** recognise faces, identify workers, score productivity or take
attendance. Garment activities come only from a trained model, and that model
is only used once it has been evaluated; **no such model exists yet and
garment accuracy is PENDING** (no labelled factory footage). A track id
(`1b22b6-t17`) is a temporary number for "a person seen by this camera", not
a worker; an activity is a prediction about that track, not verified work.
Nothing it reports changes pay, attendance or discipline.

## Contents

1. [Architecture](#architecture)
2. [Folder structure](#folder-structure)
3. [Setup (Python, dependencies, model)](#setup)
4. [CPU and CUDA](#cpu-and-cuda)
5. [Connecting to the backend](#connecting-to-the-backend)
6. [Running the service](#running-the-service)
7. [Processing a camera: recorded video, webcam, RTSP](#processing-a-camera)
8. [API](#api)
9. [Events sent to the backend](#events-sent-to-the-backend)
10. [Activity recognition](#activity-recognition)
11. [Garment activity recognition](#garment-activity-recognition)
12. [Tests](#tests)
13. [Troubleshooting](#troubleshooting)
14. [Performance and limitations](#performance-and-limitations)
15. [Licensing](#licensing)

## Architecture

```text
 platform (ASP.NET Core)                         this service (FastAPI)
 ───────────────────────                         ──────────────────────
 GET /api/integrations/cv/cameras  ──config──▶   SessionManager ── one CameraSession thread per camera
 (cameras, sources, zones; service key only)        │
                                                    ▼
                                    video source (recorded video / webcam / RTSP)
                                                    ▼   frame skipping to CV_PROCESS_FPS
                                    YOLO person detection   (one shared model, serialised)
                                                    ▼
                                    ByteTrack               (state per camera)
                                                    ▼
                                    zone association        (feet point in polygon)
                                                    ▼
                                    track lifecycle → events (detected, updated, lost, zone in/out)
                                                    ▼   optional, at CV_ACTIVITY_PROCESS_FPS, confirmed tracks only
                                    YOLO pose → features → temporal buffer per (camera, track)
                                    → classifier → activity state → activity events
                                                    ▼
 POST /api/monitoring/events  ◀──── HttpEventPublisher (queue + retry, own thread)
        │  validated: service key, factory reach, camera active, zone on camera, REAL only, no worker ids
        ▼
 SignalR /hubs/monitoring ──▶ Central Monitoring Room (live feed, timeline, alerts)
```

* The service never touches PostgreSQL. Cameras and zones come from the
  integration API; events go through the same ingestion pipeline every
  producer uses, so all platform rules apply.
* It signs in with a **service key** (`X-Service-Key`), not a person's login.
  The key reaches only the factories in the backend's `CV_SERVICE_FACTORIES`
  and may only send events and read camera configuration.

## Folder structure

```text
computer-vision/
├── app/
│   ├── main.py                 FastAPI app factory, startup / graceful shutdown
│   ├── api/                    routes.py (endpoints), schemas.py (request / response models)
│   ├── core/                   config.py (CV_* settings), device.py (CPU / CUDA), security.py (X-API-Key),
│   │                           logging.py (redacts URL credentials and keys), errors.py
│   ├── detection/              detector.py (YOLO, person class only), types.py (boxes, validation)
│   ├── tracking/               base.py (tracker contract), bytetrack.py (ByteTrack per camera),
│   │                           lifecycle.py (detected / updated / lost / zone changes)
│   ├── zones/                  geometry.py (point in polygon), association.py (overlap rule), types.py
│   ├── camera/                 sources.py (video sources), session.py (per-camera loop),
│   │                           manager.py (start / stop / limits / shutdown), metrics.py (FPS, latency)
│   ├── events/                 schemas.py (backend contract), builder.py, publisher.py (queue + retries)
│   ├── integration/            backend.py (service-key client), models.py (camera configuration)
│   ├── activity_recognition/   pose_estimator.py (YOLO pose, track association), pose_preprocessor.py (validation,
│   │                           normalisation), feature_extractor.py, temporal_buffer.py, activity_classifier.py
│   │                           (baseline + sequence-model interface), state_manager.py, activity_service.py (per camera),
│   │                           activity_config.py, schemas.py, evaluation.py (metrics, splits), offline.py (clip runs)
│   ├── garment_activities/     activity_labels.py, config.py, schemas.py, video_preprocessor.py, clip_generator.py,
│   │                           feature_extractor.py, dataset_validator.py, dataset_split.py, dataset_loader.py,
│   │                           model_factory.py, trainer.py, evaluator.py, inference.py, activity_service.py
│   └── preview/                overlay.py (development preview drawing: boxes, ids, zones, skeletons, activity)
├── models/                     weights (downloaded, not in Git); pose/ for the pose model, activity/ and garment_activity/ for trained models
├── configs/                    garment_activity_labels.json, garment_training.example.json, garment_inference.example.json
├── data/                       sample videos, temporary working copies, activity_dataset/ and garment_activity/ (conventions only; not in Git)
├── docs/                       ACTIVITY_RECOGNITION.md (generic posture), GARMENT_ACTIVITIES.md (garment pipeline end to end)
├── scripts/                    download_model.py, make_sample_video.py, benchmark_activity.py, evaluate_activity.py,
│                               activity_dataset.py, garment.py (garment dataset / training / evaluation / inference CLI)
├── tests/                      pytest suite (194 unit tests + 8 real-model tests)
├── requirements.txt, requirements-dev.txt, pytest.ini
├── .env.example                every setting, no secrets
├── Dockerfile                  CPU image
└── README.md
```

## Setup

Python **3.11** (3.11–3.12 recommended; PyTorch 2.6 wheels).

```powershell
cd computer-vision
py -3.11 -m venv .venv
.venv\Scripts\activate                     # Linux / macOS: source .venv/bin/activate

# 1. PyTorch first (CPU build; see "CPU and CUDA" for GPUs)
pip install torch==2.6.0 torchvision==0.21.0 --index-url https://download.pytorch.org/whl/cpu
# 2. everything else (+ pytest)
pip install -r requirements-dev.txt

# 3. model weights (COCO-pretrained YOLO11n, 5.4 MB) into models/
python scripts/download_model.py
# optional: bigger / more accurate
python scripts/download_model.py yolo11s.pt     # then CV_MODEL_PATH=models/yolo11s.pt
# activity recognition: the pose model (YOLO11n-pose, 6 MB) into models/pose/
python scripts/download_model.py --pose

# 4. settings
copy .env.example .env                     # then edit .env (keys below)
```

## CPU and CUDA

`CV_DEVICE=auto` (default) uses the first CUDA GPU when PyTorch sees one and
otherwise the CPU. `cuda` without a GPU falls back to CPU with a note.
`GET /system/device` shows what is in use:

```json
{"device": "cpu", "requested": "auto", "cuda_available": false, "torch_version": "2.6.0+cpu",
 "note": "No CUDA GPU available: running on CPU."}
```

For an NVIDIA GPU, install the CUDA build of the same PyTorch version instead
of the CPU one (driver ≥ 550 for CUDA 12.4):

```powershell
pip install torch==2.6.0 torchvision==0.21.0 --index-url https://download.pytorch.org/whl/cu124
pip install -r requirements-dev.txt
```

## Connecting to the backend

1. Generate a key (≥ 32 characters):
   `python -c "import secrets; print(secrets.token_urlsafe(36))"`
2. In the **backend's** `.env` (repo root):
   ```
   CV_SERVICE_KEY=<the key>
   CV_SERVICE_FACTORIES=MAIN          # factory codes, comma separated, or * for all
   ```
   Restart the API. Its log warns at startup if the key is missing or too short.
3. In **computer-vision/.env**:
   ```
   CV_BACKEND_URL=http://localhost:5000
   CV_BACKEND_SERVICE_KEY=<the same key>
   CV_API_KEY=<another random value, ≥ 24 characters, for this service's own API>
   ```

The backend then lets the service:
* read `GET /api/integrations/cv/cameras[/{id}[/video]]`: the cameras of its
  factories with their current source (RTSP URLs **with** their decrypted
  credentials, which no user-facing API ever returns), active zones (polygons
  in 0–1 coordinates) and covered workstations;
* send `POST /api/monitoring/events`: stored with producer `CV_SERVICE`,
  source REAL.

and refuses: another factory (403), a deactivated camera (409
`camera_not_active`), simulated events, device / activity events
(`event_type_not_allowed_for_service`) and any worker id
(`cv_cannot_identify`). People can't call the integration endpoints at all
(403), whatever their permissions.

## Running the service

```powershell
cd computer-vision
.venv\Scripts\python -m uvicorn app.main:create_app --factory --host 127.0.0.1 --port 8100
```

Startup loads the model once (shared by all cameras) and logs the device.
Docker:

```bash
docker build -t bunonbrain-cv computer-vision
docker run --rm -p 8100:8100 --env-file computer-vision/.env -e CV_HOST=0.0.0.0 \
  -e CV_BACKEND_URL=http://host.docker.internal:5000 \
  -v "$PWD/computer-vision/models:/app/models:ro" bunonbrain-cv
```

Stop with Ctrl+C: every camera session stops, sources are released and queued
events are flushed (up to 10 s).

## Processing a camera

Cameras are not configured here. Register them in the platform (**Cameras &
devices**), give them a video source and zones; the service reads that.

In PowerShell, set the key once: `$h = @{ "X-API-Key" = "<CV_API_KEY>" }`.

```powershell
# What can be processed
Invoke-RestMethod http://127.0.0.1:8100/cameras -Headers $h
# Start (optional body: {"process_fps": 8, "loop": false, "activity": true})
Invoke-RestMethod -Method Post http://127.0.0.1:8100/cameras/<cameraId>/start -Headers $h
Invoke-RestMethod http://127.0.0.1:8100/cameras/<cameraId>/status  -Headers $h
Invoke-RestMethod http://127.0.0.1:8100/cameras/<cameraId>/metrics -Headers $h
Invoke-RestMethod -Method Post http://127.0.0.1:8100/cameras/<cameraId>/stop -Headers $h
```

**Recorded video** (best for the prototype): on the camera's page in the
platform, *Change source → Recorded video* and upload an MP4 / WebM. Starting
the camera downloads a temporary working copy (deleted when it stops) and
plays it at its own speed. `"loop": true` repeats it. Test videos:
`python scripts/make_sample_video.py` writes `data/samples/people_walkthrough.mp4`
(several real people entering and leaving, a 1 s occlusion) and
`data/samples/two_people.mp4` (a second, independent source), made from the
photos that ship with Ultralytics.

**Local webcam**: set the camera's source to *Local webcam*. The service opens
the webcam of the machine it runs on: the number in the webcam label
("USB camera 1" → 1), else `CV_WEBCAM_INDEX` (0 = default camera). Windows may
ask for camera permission the first time.

**RTSP / HTTP stream**: set the source to *RTSP stream* with its URL (and user
name / password, stored encrypted by the platform). The service connects with
OpenCV / FFmpeg; a dropped stream is retried `CV_RECONNECT_ATTEMPTS` times and
the camera is reported OFFLINE (a `CAMERA_OFFLINE` alert) until frames return.

**Development shortcut** (`CV_ALLOW_SOURCE_OVERRIDE=true`, never in
production): start a registered camera with another source without changing
the platform:
`{"source": {"type": "file", "path": "samples/two_people.mp4"}}` (inside
`data/`) or `{"source": {"type": "webcam", "index": 0}}`.

**Development preview** (`CV_ENABLE_PREVIEW=true`): `GET
/cameras/{id}/preview.jpg` (API key) returns the last analysed frame with
boxes, temporary track ids, confidence, zones and FPS, labelled *CV
DEVELOPMENT PREVIEW*; with activity recognition also each track's pose
skeleton and predicted activity with its confidence, and the activity rate.
`?pose=false` (or `CV_PREVIEW_POSE=false`) hides the skeletons.
`CV_PREVIEW_WINDOW=true` opens a live OpenCV window per camera on the
service's machine. Neither is shown to dashboard users; the monitoring room
only receives events, and keypoints never leave the service.

## API

All endpoints except `/health` need `X-API-Key: <CV_API_KEY>`. Errors are
`{"code": "...", "message": "..."}`.

| Method | Path | What |
|---|---|---|
| GET | `/health` | Public liveness: status, model loaded, sessions running |
| GET | `/health/model` | Model name / version / device, load error, inference count, average ms; `pose`: the pose model and activity settings |
| GET | `/system/device` | CPU / CUDA in use, GPU name, PyTorch version |
| GET | `/cameras` | Cameras from the platform (no URLs or credentials) and their session state |
| POST | `/cameras/{id}/start` | Start processing (202). 409 already running, 404 unknown, 422 bad source, 429 too many, 503 model / backend unavailable |
| POST | `/cameras/{id}/stop` | Stop processing (stopping is not "camera offline") |
| GET | `/cameras/{id}/status` | STARTING / RUNNING / RECONNECTING / STOPPED / FINISHED / FAILED, error, active tracks, FPS |
| GET | `/cameras/{id}/metrics` | Frames read / processed / skipped, real processing FPS, latency, reconnects, events; `activity`: pose inferences, pose / step latency, activity rate, predictions, UNKNOWN rate and reasons |
| GET | `/cameras/{id}/activity` | Current predicted activity of each temporary track, pending change, last prediction quality, recent transitions (no keypoints) |
| GET | `/metrics` | All sessions, model, publisher (delivered / rejected / failed / queued), memory, threads |
| POST | `/config/refresh` | Re-read camera configuration (zones, status) for running cameras now (also every `CV_CONFIG_REFRESH_SECONDS`) |
| GET | `/cameras/{id}/preview.jpg` | Development preview (only with `CV_ENABLE_PREVIEW=true`); `?pose=false` hides skeletons |

Interactive docs: http://127.0.0.1:8100/docs.

## Events sent to the backend

Every event: `eventType`, `factoryId`, `cameraId`, `occurredAt` (UTC),
`sourceType: "REAL"`, `modelName` / `modelVersion`, an `idempotencyKey`
(`cv:<session>:<camera>:<seq>`, stable across retries), and a payload with the
producer's own `cvEventId`. Person events add `trackId`, `confidence` and the
box:

```json
{
  "eventType": "ZONE_ENTERED",
  "factoryId": "3f1c2a9e-…",
  "cameraId": "36c13fed-…",
  "zoneId": "8e2b…",
  "trackId": "1b22b6-t3",
  "confidence": 0.84,
  "occurredAt": "2026-11-14T18:26:40.512Z",
  "sourceType": "REAL",
  "modelName": "yolo11n",
  "modelVersion": "ultralytics-8.3.253",
  "idempotencyKey": "cv:1b22b6:CAM-001:000007",
  "payload": {
    "bbox": {"x": 0.5273, "y": 0.3694, "w": 0.0563, "h": 0.4278},
    "bottomCentre": {"x": 0.5555, "y": 0.7972},
    "frame": {"index": 41, "width": 1280, "height": 720},
    "zone": {"name": "Line 1 walkway", "type": "Walkway", "workstation": null},
    "processingFps": 7.95,
    "note": "Track ids are temporary camera-local numbers, not worker identities.",
    "cvEventId": "5f0c…", "producer": "computer-vision", "device": "cpu"
  }
}
```

| Event | When |
|---|---|
| `PERSON_DETECTED` | A new track has been seen in `CV_MIN_HITS` frames (one-frame flickers never become events) |
| `TRACK_UPDATED` | At most every `CV_TRACK_UPDATE_SECONDS` while a track stays visible |
| `PERSON_LOST` | Not seen for `CV_LOST_AFTER_SECONDS`, or the analysis stopped (payload `visibleSeconds`) |
| `ZONE_ENTERED` / `ZONE_EXITED` | The track's zone changed for `CV_ZONE_DEBOUNCE_FRAMES` frames |
| `CAMERA_STATUS_CHANGED` | ONLINE when frames flow; OFFLINE when the source can't be opened or drops |
| `MODEL_STATUS_CHANGED` | READY once per factory when the first camera of that factory starts (and once for the pose model when activity recognition starts there) |
| `ACTIVITY_STATE_CHANGED` | A track's predicted activity changed and the new one held for `CV_ACTIVITY_MIN_STATE_SECONDS` |
| `ACTIVITY_UNCERTAIN` | A track's activity became UNKNOWN for `CV_ACTIVITY_UNKNOWN_AFTER_SECONDS` (`reason`, `candidate`) |
| `ACTIVITY_CLASSIFIED` | A stable activity re-confirmed, at most every `CV_ACTIVITY_REPORT_SECONDS` |

**Zones.** A person's position is the bottom-centre of their box (their feet),
in 0–1 frame coordinates. If zones overlap, the **smallest** zone containing
the point wins (the most specific place); equal areas go by name, then id. No
zone means unassigned (no `zoneId`). Zone presence says only that a detected
person is in that part of the picture: not who, not that they work there, not
productivity, not attendance.

## Activity recognition

Full design, dataset, annotation, training and evaluation guide:
**[docs/ACTIVITY_RECOGNITION.md](docs/ACTIVITY_RECOGNITION.md)**.

**What it does.** For each confirmed track of a camera with activity
recognition on, a YOLO11 pose model (COCO-17 keypoints, same Ultralytics /
PyTorch stack, run once per frame and matched to track boxes by overlap)
gives keypoints; they are validated, normalised to the track's box and turned
into angles and proportions; the last few seconds per (camera, track) are
classified by `pose-temporal-baseline` 1.0, a transparent rule set over
posture and movement; a state manager accepts a change only when it lasts,
and the result is published as events.

| Activity | Evidence |
|---|---|
| `STANDING` | Upright torso, straight legs visible, little movement |
| `SITTING` | Thigh near horizontal or foreshortened, bent knees, torso not bent forward |
| `WALKING` | The feet point moves ≥ 0.3 body heights per second, body upright |
| `BENDING` | Torso inclined forward (≳ 30–55°) or head below shoulder level |
| `REACHING` | A wrist above its shoulder, or stretched far out above hip level |
| `UNKNOWN` | Too few usable poses, too short, confidence < 0.55, or two activities too close |

Every prediction carries a confidence, its observation window and quality;
missing or low-confidence poses never become a prediction. Garment-work
labels (`SEWING_LIKE_MOTION`, `HANDLING_MATERIAL`, `OPERATING_MACHINE`,
`PICKING_ITEM`) are reserved for future trained models: this service never
produces them and the backend refuses them from it.

**Enable it.**

```powershell
python scripts/download_model.py --pose
# computer-vision/.env
CV_ACTIVITY_ENABLED=true
CV_ACTIVITY_CAMERAS=*            # or CAM-001,CAM-004 (codes or ids)
CV_PROCESS_FPS=5                 # on a CPU: see Performance
CV_ACTIVITY_PROCESS_FPS=2.5
```

Restart the service (the pose model loads once at startup; `/health/model`
→ `pose.loaded`). A camera then uses activity recognition if it is listed in
`CV_ACTIVITY_CAMERAS`; a start request overrides it either way
(`{"activity": true}` / `{"activity": false}`; `true` without a loaded pose
model is refused with 503 `pose_model_not_ready`). The camera's status shows
`activityEnabled` and, if the pose model kept failing, `activityError`
(detection carries on).

| Setting | Default | |
|---|---|---|
| `CV_ACTIVITY_MODEL_PATH` | `models/pose/yolo11n-pose.pt` | Any COCO-17 YOLO pose checkpoint |
| `CV_ACTIVITY_PROCESS_FPS` | 4 | Pose inferences per second per camera, independent of capture and detection rates |
| `CV_ACTIVITY_WINDOW_SECONDS` | 3 | Temporal window |
| `CV_ACTIVITY_MIN_WINDOW_SECONDS` / `_MIN_OBSERVATIONS` | 1 / 4 | Evidence needed before any prediction |
| `CV_ACTIVITY_MAX_GAP_SECONDS` | 1.5 | A longer gap restarts a track's window |
| `CV_ACTIVITY_RESET_AFTER_SECONDS` | 3 | A track unseen this long loses its buffer and state |
| `CV_ACTIVITY_MIN_CONFIDENCE` / `_CONFLICT_MARGIN` | 0.55 / 0.15 | UNKNOWN thresholds |
| `CV_ACTIVITY_MIN_STATE_SECONDS` / `_UNKNOWN_AFTER_SECONDS` | 1.5 / 3 | Hysteresis before a state change / before UNKNOWN |
| `CV_ACTIVITY_REPORT_SECONDS` | 15 | Re-confirmation interval of a stable state |
| `CV_ACTIVITY_KEYPOINT_CONFIDENCE`, `_POSE_CONFIDENCE`, `_MATCH_IOU`, `_WALKING_SPEED` | 0.35, 0.35, 0.3, 0.3 | Keypoint / box thresholds, pose↔track overlap, walking speed (body heights/s) |
| `CV_ACTIVITY_CLASSIFIER` | `baseline` | `sequence` + `CV_ACTIVITY_SEQUENCE_MODEL_PATH`: a trained TorchScript LSTM / GRU / TCN (none exists yet) |

**Evaluation: pending.** There is no labelled factory dataset, so no
accuracy is claimed. `python scripts/evaluate_activity.py` says so and lists
the workflow; with an annotated, group-split dataset it reports precision,
recall, F1, the confusion matrix, per-class results, the UNKNOWN rate,
latency and FPS (see the docs).

## Garment activity recognition

Full guide (architecture, classes, annotation guidelines with ambiguous
cases, split method, model choice, training, evaluation, inference,
hardware, limitations): **[docs/GARMENT_ACTIVITIES.md](docs/GARMENT_ACTIVITIES.md)**.

Classes (configurable in `configs/garment_activity_labels.json`, factory
classes can be added): SEWING, FABRIC_HANDLING, CUTTING, IRONING,
MATERIAL_PICKING, MACHINE_OPERATION, QUALITY_INSPECTION, PACKING, WALKING,
STANDING, SITTING, UNKNOWN.

Baseline: a frozen ImageNet **MobileNetV3-Small embedding of the person
crop + pose features + motion → GRU** (`tcn` and `pose_gru` heads too),
trained on cached clip features. It runs on a CPU; video models (SlowFast,
X3D, transformers) can be added behind the same interface once there is a
GPU and enough labelled data.

```powershell
.venv\Scripts\python scripts\garment.py init
.venv\Scripts\python scripts\garment.py import D:\footage\line3_cam2.mp4 --camera CAM-002 --recording-group L3C2-2026-11-20
.venv\Scripts\python scripts\garment.py cut <video_id> --duration 4 --export        # windows + annotation template
#   annotate data\garment_activity\annotations.csv (templates and guidelines in the docs)
.venv\Scripts\python scripts\garment.py validate
.venv\Scripts\python scripts\garment.py clips --config configs\garment_training.json
.venv\Scripts\python scripts\garment.py split --seed 13
.venv\Scripts\python scripts\garment.py features --config configs\garment_training.json
.venv\Scripts\python scripts\garment.py train --config configs\garment_training.json
.venv\Scripts\python scripts\garment.py evaluate models\garment_activity\garment-cnn-gru\0.1.0
.venv\Scripts\python scripts\garment.py infer models\garment_activity\garment-cnn-gru\0.1.0 data\my_clip.mp4
```

Live: `CV_GARMENT_ENABLED=true`, `CV_GARMENT_CONFIG=configs/garment_inference.json`
(per-camera model and version, inference rate, thresholds; copy the
example). Only models evaluated on the held-out test split are loaded.
Garment recognition reuses the pose stage and publishes
`GARMENT_ACTIVITY_CHANGED` / `_UNCERTAIN` / `_CLASSIFIED` with the model's
name, version, dataset version and evaluation status, plus workstation
context kept separate from the prediction; generic posture events continue
alongside. `GET /cameras/{id}/garment` shows the current predictions.

## Tests

```powershell
.venv\Scripts\python -m pytest -m "not model"   # 194 unit tests, no model needed (~35 s)
.venv\Scripts\python -m pytest -m model         # 8 real-model tests (needs models/yolo11n.pt, models/pose/yolo11n-pose.pt, the
                                                 # MobileNetV3 ImageNet weights torchvision downloads once, + sample videos)
.venv\Scripts\python -m pytest                  # all
```

Unit tests replace only inference and video input: ByteTrack, zones,
lifecycle, events, publishing and the API are the real code. They cover
person-class filtering, box validation, track ids across frames, occlusion,
new entries, independent cameras, zone geometry and overlaps, event schema,
publishing failures (rejections, outages, full queue), start / stop / duplicate
/ limits, invalid and dropped sources, API authentication, CPU fallback, log
redaction and graceful shutdown.

Activity recognition is tested with the pose model mocked (synthetic COCO-17
skeletons through the real preprocessing, features, classifier, buffer,
state manager, sessions and API): keypoint validation and normalisation,
missing / low-confidence keypoints, each activity, movement from timestamps,
UNKNOWN reasons, thresholds, buffer isolation per (camera, track) and across
cameras, gaps and expiry, state transitions and hysteresis, event schema and
publishing, per-camera enable / disable, pose-model failure containment,
graceful shutdown, metrics, evaluation metrics and leakage-safe splits.

The real-model tests run YOLO11n on the Ultralytics sample photos (4 people in
`bus.jpg`, none in an empty frame), the full pipeline on
`people_walkthrough.mp4` (several tracks, zone entry, loss), YOLO11n-pose on
`bus.jpg` (keypoints of real people; a detection model is refused as a pose
model) and the full pipeline with activity recognition on the sample video
(activity events that pass the contract). How to test with your own
recordings: docs/ACTIVITY_RECOGNITION.md, section 14.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `/health` says `degraded`, `/health/model` error "Model weights not found" | Run `python scripts/download_model.py`, check `CV_MODEL_PATH` |
| start → 503 `backend_auth_failed` | `CV_BACKEND_SERVICE_KEY` ≠ backend `CV_SERVICE_KEY`, or the API wasn't restarted after setting it |
| start → 404 `camera_not_found` | The camera isn't in a factory listed in the backend's `CV_SERVICE_FACTORIES` |
| start → 422 `no_source` / "has no recorded video uploaded" | Configure the camera's video source in the platform |
| start → 422 `camera_not_active` | The camera is deactivated / in maintenance in the platform |
| Session FAILED "Can't open local webcam 0" | No webcam, another app is using it, or Windows camera privacy settings block desktop apps |
| Session RECONNECTING then FAILED for RTSP | Network / credentials / codec: test the URL in VLC; OpenCV needs an FFmpeg-readable stream |
| `/metrics` publisher `rejected` > 0 | See `last_error` (e.g. `400 zone_inactive` after a zone was deactivated; it refreshes within `CV_CONFIG_REFRESH_SECONDS`, or call `/config/refresh`) |
| Many different track ids for the same person | Raise `CV_PROCESS_FPS` (fast motion between analysed frames breaks association) |
| Slow / high CPU | Lower `CV_PROCESS_FPS`, `CV_IMAGE_SIZE=480`, fewer cameras, or use a GPU |
| start → 503 `pose_model_not_ready` | `CV_ACTIVITY_ENABLED=false`, or `models/pose/yolo11n-pose.pt` missing (`python scripts/download_model.py --pose`); see `/health/model` → `pose.error` |
| `activityEnabled` false, no activity events | The camera isn't in `CV_ACTIVITY_CAMERAS`, or start it with `{"activity": true}` |
| Mostly `ACTIVITY_UNCERTAIN` | Legs / torso hidden, low resolution or a steep angle: check `/cameras/{id}/activity` → `lastPrediction.reason` and the preview's skeletons; move the camera, raise `CV_ACTIVITY_IMAGE_SIZE`, or accept UNKNOWN for that view |
| Status `activityError` | The pose model failed 5 times in a row on that camera; see the log; restart the camera |
| Backend rejects `activity_not_supported_by_service` / `activity_invalid` | Only STANDING, SITTING, WALKING, BENDING, REACHING, UNKNOWN are accepted from this service |

## Performance and limitations

Measured on the development laptop (CPU only, 8 threads, YOLO11n, 640 px):
**~83 ms per inference (~11–12 frames/s in total)**; one camera at 8 fps
used ~121 ms per processed frame including tracking.

With activity recognition (`python scripts/benchmark_activity.py`, the 12 s
`people_walkthrough.mp4` played at its own speed, CPU, 2026-11-15; the
machine was under memory pressure, so the detector took ~100–115 ms):

| Setting | Achieved processing FPS | Avg per processed frame | Pose inference | Real-time? |
|---|---|---|---|---|
| Detection 8 fps, activity off | 7.5 | 116 ms | — | yes |
| Detection 8 fps + pose 4 fps | 4.7 (video took 37.7 s) | 201 ms | 121 ms | **no**: 8×110 + 4×120 ms > 1 s |
| Detection 5 fps, activity off | 4.7 | 111 ms | — | yes |
| Detection 5 fps + pose 2.5 fps | 5.0 (12.1 s) | 139 ms | 102 ms | yes |

So on this CPU one camera with activity recognition needs
`CV_PROCESS_FPS=5` / `CV_ACTIVITY_PROCESS_FPS=2.5`; several cameras with
activity need a GPU. Pose inference runs only when confirmed tracks are in
view and is shared (serialised) across cameras. On that sample (panned
photographs, not factory footage) 61–78 % of window predictions were
UNKNOWN, mostly low confidence or too-short history, and the camera pan read
as walking: this is a throughput measurement, not an accuracy one. The model is shared and
inference is serialised, so cameras share that budget: 2 cameras × 5 fps fits,
4 cameras × 8 fps does not on this CPU (frames are then skipped, latency grows).
A GPU raises the budget by an order of magnitude.

* Tracking quality depends on the analysed frame rate: in the sample video
  (fast pan) 5 fps split 6 people into 13 tracks, 7.5 fps or more kept exactly
  6. Real workers move slower, but keep `CV_PROCESS_FPS` ≥ 8 for walking people.
* Track ids restart when a camera restarts, and the same person gets different
  ids on different cameras: there is no re-identification, by design.
* One process: sessions are threads, the model is one instance. Scaling past
  one machine's budget needs several service instances, each with its own
  cameras (no coordination between instances yet).
* Live RTSP needs a stream OpenCV / FFmpeg can decode; it was not tested
  against physical cameras in this phase. Local webcams work on the service's
  own machine only.
* Recorded videos are downloaded whole before processing (platform limit
  `CAMERA_VIDEO_MAX_MB`, default 50 MB).
* Events are delivered at least once: the backend stores one per idempotency
  key, so retries never duplicate. A queue overflow (> `CV_PUBLISH_QUEUE_SIZE`
  pending) drops new events and counts them.
* Privacy: no face recognition, no embeddings, no audio, no stored video (only
  a temporary working copy of an uploaded recording during processing), no
  identities. Keypoints stay in memory for the current window only and are
  never sent; the preview is for developers only.
* Activity recognition: a rule-based baseline, not trained and not yet
  evaluated on factory footage; fixed cameras assumed (camera motion reads as
  walking); hidden legs, top-down and face-on seated views are hard (expect
  UNKNOWN); reaching toward the camera is invisible to 2-D keypoints; a track
  id switch restarts the person's state. Future: a trained temporal model on
  labelled factory clips, per-camera calibration, hand / object-aware models
  for garment operations (separately validated), GPU deployment, sharing one
  inference between detection and pose.

## Licensing

Ultralytics YOLO and its pretrained weights (detection and pose) are licensed **AGPL-3.0**. Using
them in a closed-source or commercially distributed product requires
complying with AGPL-3.0 (publishing the source of the whole service) or an
Ultralytics Enterprise License. Decide this before production use.
