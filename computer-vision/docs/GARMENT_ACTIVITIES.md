# Garment-specific activity recognition

A trained-model pipeline that predicts garment manufacturing activities
(sewing, fabric handling, cutting, ironing, material picking, machine
operation, quality inspection, packing) and movement/posture fallbacks
(walking, standing, sitting) for tracked people, or UNKNOWN.

**Status: no garment model exists yet, and garment-specific accuracy is
PENDING.** The dataset, training, evaluation and live-inference
infrastructure is complete and tested. A model needs labelled
garment-factory footage, which this project doesn't have. Nothing here is
trained on fabricated data, and no accuracy is claimed.

This is separate from the generic posture recognition
(docs/ACTIVITY_RECOGNITION.md), which stays available as the fallback and
keeps its own events. Garment predictions never replace it silently.

## Contents

1. [Architecture](#1-architecture)
2. [Activity classes](#2-activity-classes)
3. [Model selection](#3-model-selection)
4. [Dataset preparation](#4-dataset-preparation)
5. [Annotation format and guidelines](#5-annotation-format-and-guidelines)
6. [Splitting without leakage](#6-splitting-without-leakage)
7. [Training](#7-training)
8. [Evaluation](#8-evaluation)
9. [Live inference](#9-live-inference)
10. [Machine and workstation context](#10-machine-and-workstation-context)
11. [Backend events](#11-backend-events)
12. [Hardware](#12-hardware)
13. [Limitations](#13-limitations)

## 1. Architecture

```text
 app/garment_activities/
 ├── activity_labels.py    label registry (configs/garment_activity_labels.json)
 ├── config.py             dataset paths, clip / feature / training / decision / inference settings (JSON)
 ├── schemas.py            videos, annotations, clips, predictions; CSV / JSONL readers and writers
 ├── video_preprocessor.py import + metadata, SHA-256 and perceptual near-duplicate signatures, frames, clip export
 ├── clip_generator.py     fixed-length windows; labelled clips from annotations (multi-label, ambiguous, unusable)
 ├── feature_extractor.py  per-frame features (CNN crop embedding + pose + motion), shared by training and live use
 ├── dataset_validator.py  annotation checks, leakage checks, class distribution
 ├── dataset_split.py      group splits (video / recording / person / near-duplicate), seeded, camera hold-out
 ├── dataset_loader.py     annotations and cached clip features → arrays
 ├── model_factory.py      model interface, heads (gru, tcn, pose_gru), checkpoints with metadata, safe loading
 ├── trainer.py            seeded training, class weights, early stopping, checkpoint + metadata
 ├── evaluator.py          held-out evaluation, report, metadata update
 ├── inference.py          a loaded model + the UNKNOWN decision rules (same offline and live)
 └── activity_service.py   per-camera live service; infer_video() for recorded video
 scripts/garment.py        the command-line workflow
 configs/                  garment_activity_labels.json, garment_training.example.json, garment_inference.example.json
```

Live pipeline (nothing is detected, tracked or pose-estimated twice):

```text
camera session ─ YOLO detection ─ ByteTrack ─ zones ─ lifecycle
                                   │
              pose stage (activity_recognition, CV_ACTIVITY_PROCESS_FPS): poses matched to track ids
                    │                                  │
   generic posture rules ─▶ ACTIVITY_* events          │  (the fallback, unchanged)
                                                       ▼
   garment service: sample at the model's fps → crop → MobileNetV3 embedding (once per observation)
   + pose + motion, per (camera, track) → every 1/inference_fps s: last clip of every track in one batch
   → model → UNKNOWN rules → state smoothing → GARMENT_ACTIVITY_* events (with model + context)
                                                       ▼
   existing publisher → POST /api/monitoring/events → SignalR → Activity page (Garment activity)
```

Every buffer and state is keyed by `(camera id, track id)`. Each camera
session has its own garment service. Models and CNN backbones are loaded once
and shared, with inference serialised. A failing model switches off garment
recognition for that camera only, after 5 consecutive errors; detection,
tracking and generic posture recognition continue.

## 2. Activity classes

`configs/garment_activity_labels.json` is the single list. Add
factory-specific classes there (`kind: task`) without code changes; the
backend accepts any upper-snake-case label and the dashboard shows it.
UNKNOWN is always present and always a valid output.

| Activity | Kind | Visible evidence required |
|---|---|---|
| SEWING | task | Hands guiding fabric under the needle / presser foot, or hand stitching. Sitting at a machine is not enough |
| FABRIC_HANDLING | task | Fabric visibly being spread, stacked, folded or carried |
| CUTTING | task | Scissors, a knife or a cutting machine moving through fabric |
| IRONING | task | An iron or press moving over / closing on a garment |
| MATERIAL_PICKING | task | Reaching into a bin, rack or trolley and taking items |
| MACHINE_OPERATION | task | Hands on a non-sewing machine's controls, loading / unloading it while it works |
| QUALITY_INSPECTION | task | A garment held up, turned over and examined, measured or compared |
| PACKING | task | Finished garments folded into shape, bagged, boxed or sealed |
| WALKING | movement | Continuous steps from one place to another |
| STANDING | posture | Upright and stationary, and no task above clearly visible |
| SITTING | posture | Seated, and no task above clearly visible |
| UNKNOWN | unknown | The task can't be seen or is ambiguous |

Rules: SITTING is not SEWING; stationary is not "idle" (there is no idle
class, and none should be inferred); a person may do several things within
seconds (multi-label annotations, short state hold times).

## 3. Model selection

| Approach | Verdict here |
|---|---|
| **Frozen CNN on person crops + pose + motion → GRU** | **Baseline.** A CPU embeds a 112 px crop with MobileNetV3-Small in ~2–4 ms; features are cached per clip, so the GRU head trains in minutes on a CPU and learns from hundreds of clips rather than tens of thousands |
| Same inputs → temporal CNN (`tcn`) | Implemented as an alternative head; the same interface |
| Pose + motion only → GRU (`pose_gru`) | Implemented as a cheap ablation; it can't see fabric, tools or machines, so it is expected to be weaker on tasks |
| CNN + LSTM | A one-line variant of the GRU head; GRU has fewer parameters for small data |
| End-to-end video models (SlowFast, X3D, video transformers) | Not implemented: they need a GPU, far more labelled clips and fine-tuning time. They plug in as another `model_factory` entry taking raw clips, without changing the camera pipeline |

Why this baseline: garment work is about **what the hands are doing with
fabric and machines**, which a skeleton alone can't see. The person crop
(enlarged by 15 % so hands, tools and the work surface are included) carries
that; pose carries body posture and hand positions; motion (feet,
box-scale and both wrists' velocity) separates repetitive hand work from
standing still. Freezing the ImageNet CNN keeps training cheap and
data-efficient. Once enough labelled data exists, fine-tuning the CNN or
moving to a video model are the next steps, judged on the same test set.

Model interface: `model(x, mask) -> logits`, where `x` is (B, T, D) per-frame
features, `mask` is (B, T) and logits are (B, classes). The feature layout is
versioned (`garment-features-1`); a model trained on another layout is refused
at load time.

## 4. Dataset preparation

```powershell
cd computer-vision
.venv\Scripts\python scripts\garment.py init
.venv\Scripts\python scripts\garment.py import D:\footage\line3_cam2_0900.mp4 --camera CAM-002 --recording-group L3C2-2026-11-20
.venv\Scripts\python scripts\garment.py import D:\footage\sewing\ --label-hint sewing      # a folder; the hint is only a folder name
.venv\Scripts\python scripts\garment.py frames v-1a2b3c4d5e6f --every 2                    # preview frames for annotators
.venv\Scripts\python scripts\garment.py cut v-1a2b3c4d5e6f --duration 4 --export           # 4 s windows → MP4 + a template CSV
#   annotate (section 5), then:
.venv\Scripts\python scripts\garment.py validate
.venv\Scripts\python scripts\garment.py clips --config configs\garment_training.json
.venv\Scripts\python scripts\garment.py split --seed 13 [--holdout-camera CAM-003]
.venv\Scripts\python scripts\garment.py features --config configs\garment_training.json
.venv\Scripts\python scripts\garment.py report
```

* **Import** copies the video to `raw/<hint or unsorted>/<video id>` (id from
  the SHA-256, so a file is imported once) and records fps, frames,
  duration, size, codec, camera, recording group and a perceptual signature
  in `videos.jsonl`. Exact duplicates are skipped; near-duplicates (re-encodes,
  trims) are reported and always kept in the same split.
* **Clips**: `duration_s` (2, 4 or 6 s are all reasonable: short actions
  like picking suit 2 s, sewing cycles 4–6 s; try them on the validation set),
  `stride_s`, `sample_fps` (T = duration × fps frames), `min_overlap`. Changing
  them means re-running `clips` and `features`.
* **Features**: for each clip the service's own detector, ByteTrack and pose
  model run over the clip (with a 1 s lead-in), the annotated subject is the
  track overlapping the annotation's ROI (or the most-seen track without an
  ROI), and every sampled frame becomes a feature vector (crop embedding,
  pose, motion, presence) saved to `processed/features/<clip>.npz` with its
  quality (person / pose ratio, subject found, extraction time).
* **Report**: clips and seconds per class per split, missing classes,
  imbalance ratio, multi-label clips (`reports/class_distribution.json`).

Never commit footage, annotations of real people, features or models:
`data/garment_activity/` and `models/garment_activity/` are ignored by Git
except their READMEs and templates.

## 5. Annotation format and guidelines

One row per **time segment of one person** in `annotations.csv` or
`annotations.jsonl` (templates in `data/garment_activity/`):

| Field | |
|---|---|
| `annotation_id` | Unique |
| `video_id` | From `videos.jsonl` |
| `clip_id` | Optional: when annotating a pre-cut `cut` window |
| `camera_id` | Optional (defaults to the video's) |
| `start_s`, `end_s` | Seconds from the video's start |
| `labels` | One or more labels (CSV: `SEWING\|FABRIC_HANDLING`); empty for AMBIGUOUS / UNUSABLE |
| `status` | `LABELLED`, `AMBIGUOUS` (can't tell → UNKNOWN) or `UNUSABLE` (blurred, blocked, no subject: no clip overlapping it is used) |
| `annotator` | A pseudonymous id (`ann-02`) |
| `confidence` | The annotator's confidence 0–1 |
| `subject_group` | A pseudonym of the person (`P017`): never a name or employee code |
| `subject_roi` (`roi_x` `roi_y` `roi_w` `roi_h`) | The person's box at `start_s`, 0–1 of the frame, when several people are in view |
| `notes` | Free text |

Overlapping segments of the same subject (or several labels) make
multi-label clips; the clip's primary label is the one covering most of it.

**Require evidence of the task itself.** When in doubt, mark AMBIGUOUS.

| Situation | Label |
|---|---|
| Sitting at a sewing machine, hands in the lap or on a phone | SITTING |
| Sitting at a machine, hands hidden by the machine head | AMBIGUOUS (not SEWING) |
| Holding a bundle while walking to the next station | WALKING (+ FABRIC_HANDLING only if the fabric is being worked on) |
| Holding fabric while talking | STANDING / SITTING, not FABRIC_HANDLING |
| Standing beside a running machine, not touching it | STANDING, not MACHINE_OPERATION |
| Trimming threads with snips at the machine | CUTTING if the snips are visible cutting; else AMBIGUOUS |
| Folding a finished garment into a bag | PACKING (not FABRIC_HANDLING) |
| Folding cut panels into a stack for the line | FABRIC_HANDLING |
| Turning a garment over and checking seams | QUALITY_INSPECTION |
| Taking trims out of a bin | MATERIAL_PICKING |
| A workstation assigned to sewing, but the person is reaching for a bundle | MATERIAL_PICKING (the assignment is not evidence) |

Have two annotators label a sample: `validate` reports where they disagree
on the same moment of the same person. If people disagree, the model can't
be expected to agree, so refine the guideline first.

## 6. Splitting without leakage

* Never by clip or frame: videos sharing a **recording group**, a **subject
  group** (the same person) or being **near-duplicates** are joined into one
  group, and whole groups go to train / validation / test (default
  70 / 15 / 15) by a seeded hash: the same seed gives the same split.
* `--holdout-camera CAM-003` puts every clip of that camera in the test set,
  to measure generalisation to an angle the model never saw. Do this before
  claiming a model works on new cameras.
* `splits.json` records the seed, ratios, method, groups, each video's split,
  counts and the **dataset version** (a hash of clips, labels and split,
  stored with every model). `train/`, `validation/` and `test/` each get the
  clip list of that split. `validate` fails on any leakage.

## 7. Training

```powershell
copy configs\garment_training.example.json configs\garment_training.json
.venv\Scripts\python scripts\garment.py train --config configs\garment_training.json
```

Configurable: architecture (`gru`, `tcn`, `pose_gru`), input parts,
backbone and input resolution (112–224), clip duration and sample rate,
batch size, learning rate, weight decay, epochs, early-stopping patience,
class weighting (inverse frequency), learning UNKNOWN as a class or not,
seed, device (`auto` uses CUDA when present).

Writes `models/garment_activity/<name>/<version>/`:

* `model.pt` — the best checkpoint (lowest validation loss);
* `metadata.json` — model name and version, dataset version, data source,
  labels, label-registry version, feature version, architecture and input
  configuration, training configuration, decision thresholds, data counts,
  validation loss / accuracy / macro F1, best epoch, training date and time,
  and `evaluation.status: PENDING`;
* `history.csv` — train and validation loss and validation accuracy per epoch.

Training refuses without real training and validation clips. Unit tests use
clearly synthetic tensors only to test the mechanics, in temporary folders.

## 8. Evaluation

```powershell
.venv\Scripts\python scripts\garment.py evaluate models\garment_activity\garment-cnn-gru\0.1.0 --split validation   # while tuning
.venv\Scripts\python scripts\garment.py evaluate models\garment_activity\garment-cnn-gru\0.1.0                       # test, once
.venv\Scripts\python scripts\garment.py export-report data\garment_activity\reports\<report>.json                   # → Markdown
```

It reports, both after the UNKNOWN rules (what the live system would say)
and from the raw arg-max (what the network alone says):

* accuracy, accuracy over decided predictions, precision, recall and F1 per
  class, macro and weighted F1, the confusion matrix (with an UNKNOWN
  column), the most frequent class-wise errors;
* the UNKNOWN rate, low-confidence rate and UNKNOWN reasons;
* per-camera results (cameras with ≥ 20 clips);
* how often a prediction matches any label of a multi-label clip;
* model latency per clip, feature-extraction latency and throughput.

Evaluating on the test split stores the summary in `metadata.json`
(`evaluation.status: EVALUATED`). Only then does the service load the model
for cameras, unless `allow_unevaluated` is set for development. Without
labelled test clips it reports **PENDING** and no score.

## 9. Live inference

```powershell
copy configs\garment_inference.example.json configs\garment_inference.json
# computer-vision/.env
CV_GARMENT_ENABLED=true
CV_GARMENT_CONFIG=configs/garment_inference.json
```

`garment_inference.json`: `default_model`, per-camera `{"enabled", "model"}`
(by camera code or id, `"*"` for all; model version = the directory),
`inference_fps` (default 1/s: each run classifies every track's last clip in
one batch), the decision thresholds, state hold times (`min_state_seconds`
4, `unknown_after_seconds` 6, `report_seconds` 30) and `allow_unevaluated`.

A start request can override it: `{"garment": true}` / `{"garment": false}`.
`true` without a usable model (missing, unevaluated, another feature
layout) is refused with 503 `garment_model_unavailable`; a configured
default degrades to "no garment" with a warning, and detection still starts.
Garment recognition reuses the pose stage, so it turns generic activity
recognition on for the camera: the posture events keep flowing as the fallback.

UNKNOWN when: the person is seen in < 60 % of the clip's frames, has a
usable pose in < 40 % (models using pose), the top probability is < 0.6, or
the margin over the runner-up is < 0.15 (all configurable). The best guess
is kept as `candidate`, never asserted.

API: `GET /cameras/{id}/garment` (current prediction, model and context per
track), `GET /cameras/{id}/metrics` → `garment` (observations, embeddings,
inference count and latency, predictions, UNKNOWN and low-confidence rates,
errors), `GET /health/model` → `garment` (loaded models, evaluation status).
The development preview adds a "garment:" line under each track.

Recorded video (no backend, nothing published):

```powershell
.venv\Scripts\python scripts\garment.py infer models\garment_activity\garment-cnn-gru\0.1.0 data\my_clip.mp4 --out preds.json
```

## 10. Machine and workstation context

Each garment event carries `payload.context`: the zone (id, name, type),
workstation id and code, and line id from the camera's zone configuration.
Machine id is reserved; it is not in the configuration yet. Context is
**never** an input to the model and never overrides it:

* a person beside a sewing machine is not proof of sewing;
* a running machine is not proof that the tracked person operates it;
* a workstation assignment is not proof of the assigned task.

The dashboard shows context and prediction side by side, labelled as such.

## 11. Backend events

`GARMENT_ACTIVITY_CLASSIFIED` (periodic confirmation),
`GARMENT_ACTIVITY_CHANGED` (an accepted change), `GARMENT_ACTIVITY_UNCERTAIN`
(became UNKNOWN; `reason`, `candidate`; never names a worker). They are VISION
events through the existing ingestion API (no new table; Python never writes
to PostgreSQL). Each event carries `cameraId`, `trackId`, the label, a
confidence (optional when uncertain), `observationStart` / `observationEnd`,
`modelName` and `modelVersion` (required), `payload.model` (dataset version,
architecture, evaluation status and macro F1), `scores`, `quality`,
`context`, `sourceType: REAL`, a `cvEventId` and a stable `idempotencyKey`.
Worker ids are never attached: the CV service can't identify anyone, and other
producers can only via a verified RFID card. See MONITORING_EVENTS.md.

## 12. Hardware

Measured on the development laptop (CPU only, 4 PyTorch threads, YOLO11n,
YOLO11n-pose, MobileNetV3-Small at 112 px). Timings use an untrained,
randomly initialised network (no trained model exists), so the predictions
are meaningless and only the cost was measured. The sample videos were
processed unpaced:

| Stage | Time |
|---|---|
| Person detection (YOLO11n, 640 px) | ~100–115 ms per frame |
| Pose (YOLO11n-pose) | ~118–130 ms per pose tick (shared with generic activity) |
| Crop embeddings (MobileNetV3-Small, all people in a frame) | ~24–33 ms per sample |
| Garment GRU head (all tracks' clips in one batch) | ~3.5 ms per run |

Pose dominates; the garment stage adds about 25–35 ms per sample on top. On
this CPU one camera with garment recognition runs at about 4–5 processed
frames/s: use `CV_PROCESS_FPS=5`, `CV_ACTIVITY_PROCESS_FPS=4`, `sample_fps`
4, `inference_fps` 1. Several cameras need a GPU (CUDA is used
automatically). Feature extraction (detector + ByteTrack + pose + CNN per
sampled frame, 4 fps, 1 s lead-in) is the slow dataset step: measured 2.4 s
for a 2 s clip, 4.9 s for 4 s and 7.9 s for 6 s, so about 1.2–1.3 s per
second of clip; 1,000 clips of 4 s take about 1.5 hours. Training the head on
the cached features is fast (seconds to minutes on a CPU for hundreds of
clips; the synthetic-tensor unit test trains 180 clips for up to 30 epochs in about 3.5 s).

## 13. Limitations

* **No trained model and no labelled data: garment accuracy is PENDING.**
* The baseline sees crops and skeletons, not fine hand detail: telling
  sewing apart from fabric handling at the machine, or trimming from
  sewing, will need close camera angles, good annotation and probably a
  fine-tuned or video model.
* Subject selection in a clip uses the ROI at the clip's start; people
  moving a lot within a long segment may be mis-assigned (annotate shorter
  segments or per clip).
* A track id switch restarts that person's buffer and state.
* Machine ids aren't in the camera configuration yet, so context has no
  machine.
* The simulator does not produce garment events; the dashboard shows them
  only once a model runs on a camera.
* Public video datasets (Kinetics etc.) were not used: none covers garment
  work, and their licences and size make them unsuitable here. Performance
  on any public dataset would say nothing about a garment factory.
