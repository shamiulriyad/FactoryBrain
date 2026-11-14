# BunonBrain computer-vision service

A separate Python service that detects **people** in camera video with a
pretrained **YOLO** model, follows them with **ByteTrack**, works out which
configured **camera zone** each person stands in, and sends structured events
to the BunonBrain ASP.NET Core backend, which shows them live in the Central
Monitoring Room.

It does **not** recognise faces, identify workers, classify activity, score
productivity or take attendance. A track id (`1b22b6-t17`) is a temporary
number for "a person seen by this camera", not a worker. Nothing it reports
changes pay, attendance or discipline.

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
10. [Tests](#tests)
11. [Troubleshooting](#troubleshooting)
12. [Performance and limitations](#performance-and-limitations)
13. [Licensing](#licensing)

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
│   └── preview/                overlay.py (development preview drawing)
├── models/                     weights (downloaded, not in Git)
├── data/                       sample videos and temporary working copies (not in Git)
├── scripts/                    download_model.py, make_sample_video.py
├── tests/                      pytest suite (49 unit tests + 3 real-model tests)
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
# Start (optional body: {"process_fps": 8, "loop": false})
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
DEVELOPMENT PREVIEW*. `CV_PREVIEW_WINDOW=true` opens a live OpenCV window per
camera on the service's machine. Neither is shown to dashboard users; the
monitoring room only receives events.

## API

All endpoints except `/health` need `X-API-Key: <CV_API_KEY>`. Errors are
`{"code": "...", "message": "..."}`.

| Method | Path | What |
|---|---|---|
| GET | `/health` | Public liveness: status, model loaded, sessions running |
| GET | `/health/model` | Model name / version / device, load error, inference count, average ms |
| GET | `/system/device` | CPU / CUDA in use, GPU name, PyTorch version |
| GET | `/cameras` | Cameras from the platform (no URLs or credentials) and their session state |
| POST | `/cameras/{id}/start` | Start processing (202). 409 already running, 404 unknown, 422 bad source, 429 too many, 503 model / backend unavailable |
| POST | `/cameras/{id}/stop` | Stop processing (stopping is not "camera offline") |
| GET | `/cameras/{id}/status` | STARTING / RUNNING / RECONNECTING / STOPPED / FINISHED / FAILED, error, active tracks, FPS |
| GET | `/cameras/{id}/metrics` | Frames read / processed / skipped, real processing FPS, latency, reconnects, events |
| GET | `/metrics` | All sessions, model, publisher (delivered / rejected / failed / queued), memory, threads |
| POST | `/config/refresh` | Re-read camera configuration (zones, status) for running cameras now (also every `CV_CONFIG_REFRESH_SECONDS`) |
| GET | `/cameras/{id}/preview.jpg` | Development preview (only with `CV_ENABLE_PREVIEW=true`) |

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
| `MODEL_STATUS_CHANGED` | READY once per factory when the first camera of that factory starts |

**Zones.** A person's position is the bottom-centre of their box (their feet),
in 0–1 frame coordinates. If zones overlap, the **smallest** zone containing
the point wins (the most specific place); equal areas go by name, then id. No
zone means unassigned (no `zoneId`). Zone presence says only that a detected
person is in that part of the picture: not who, not that they work there, not
productivity, not attendance.

## Tests

```powershell
.venv\Scripts\python -m pytest -m "not model"   # 49 unit tests, no model needed (~12 s)
.venv\Scripts\python -m pytest -m model         # 3 real-model tests (needs models/yolo11n.pt + sample videos)
.venv\Scripts\python -m pytest                  # all
```

Unit tests replace only inference and video input: ByteTrack, zones,
lifecycle, events, publishing and the API are the real code. They cover
person-class filtering, box validation, track ids across frames, occlusion,
new entries, independent cameras, zone geometry and overlaps, event schema,
publishing failures (rejections, outages, full queue), start / stop / duplicate
/ limits, invalid and dropped sources, API authentication, CPU fallback, log
redaction and graceful shutdown.

The real-model tests run YOLO11n on the Ultralytics sample photos (4 people in
`bus.jpg`, none in an empty frame) and the full pipeline on
`people_walkthrough.mp4` (several tracks, zone entry, loss).

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

## Performance and limitations

Measured on the development laptop (CPU only, 8 threads, YOLO11n, 640 px):
**~83 ms per inference (~11–12 frames/s in total)**; one camera at 8 fps
used ~121 ms per processed frame including tracking. The model is shared and
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
  identities. The preview is for developers only.

## Licensing

Ultralytics YOLO and its pretrained weights are licensed **AGPL-3.0**. Using
them in a closed-source or commercially distributed product requires
complying with AGPL-3.0 (publishing the source of the whole service) or an
Ultralytics Enterprise License. Decide this before production use.
