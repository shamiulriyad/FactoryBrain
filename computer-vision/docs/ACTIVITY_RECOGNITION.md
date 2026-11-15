# Activity recognition: design, datasets, training and evaluation

This is the first version of worker activity recognition in the
computer-vision service. It predicts **basic physical posture and movement**
of tracked people: `STANDING`, `SITTING`, `WALKING`, `BENDING`, `REACHING`,
or `UNKNOWN` when the evidence is not good enough.

It does **not** detect sewing or any other garment operation, does not
identify anyone, and does not measure productivity, performance or
attendance. Those need separate, validated phases.

**Evaluation status: pending.** No labelled factory dataset exists yet, so
no accuracy figure exists. The classifier is a hand-written rule set; treat
its output as a first approximation until it has been evaluated as described
below.

## Contents

1. [Pipeline](#1-pipeline)
2. [Pose model selection](#2-pose-model-selection)
3. [Features](#3-features)
4. [Temporal classification](#4-temporal-classification)
5. [Confidence and UNKNOWN](#5-confidence-and-unknown)
6. [Activity state](#6-activity-state)
7. [Events](#7-events)
8. [Why pose estimation is not activity recognition](#8-why-pose-estimation-is-not-activity-recognition)
9. [Dataset: collection and annotation](#9-dataset-collection-and-annotation)
10. [Splitting without leakage](#10-splitting-without-leakage)
11. [Class imbalance](#11-class-imbalance)
12. [Training a temporal model](#12-training-a-temporal-model)
13. [Evaluation](#13-evaluation)
14. [Real-model integration tests with recorded videos](#14-real-model-integration-tests-with-recorded-videos)
15. [Limitations](#15-limitations)

## 1. Pipeline

```text
camera session (one thread per camera, unchanged)
  frame ─▶ YOLO person detection ─▶ ByteTrack ─▶ zones ─▶ lifecycle (detected / lost / zone events)
                                         │
                       (CV_ACTIVITY_PROCESS_FPS, only if confirmed tracks are visible)
                                         ▼
  pose_estimator.py      YOLO pose on the whole frame, once; poses matched to track boxes by IoU
  pose_preprocessor.py   validate keypoints, drop low-confidence ones, normalise to the track's box
  feature_extractor.py   angles and proportions per frame (missing parts stay missing)
  temporal_buffer.py     last CV_ACTIVITY_WINDOW_SECONDS per (camera, track)
  activity_classifier.py window → scores → prediction or UNKNOWN (with the reason)
  state_manager.py       hysteresis: minimum durations, smoothing, transitions, history
  activity_service.py    the per-camera orchestration, metrics, failure containment
                                         ▼
  events/builder.py      ACTIVITY_STATE_CHANGED / ACTIVITY_UNCERTAIN / ACTIVITY_CLASSIFIED
                                         ▼
  existing publisher ─▶ POST /api/monitoring/events ─▶ SignalR ─▶ Monitoring room → Activity
```

* It reuses the camera's session, detector, ByteTrack tracker, lifecycle,
  event builder and publisher. It never creates track ids: a pose gets the id
  of the track it overlaps, and a pose that overlaps no track is ignored.
* Every buffer and state is keyed by `(camera id, track id)`, and each
  camera session has its own activity service, so tracks of different
  cameras never mix.
* The pose model and the classifier are loaded once and shared by all
  cameras (inference is serialised, like the person detector).
* Pose inference runs at `CV_ACTIVITY_PROCESS_FPS` (default 4/s), separately
  from the detection rate and the camera's capture rate, and is skipped when
  no confirmed track is in view.
* A pose-model failure is contained: after 5 consecutive errors the camera's
  activity recognition switches itself off (`activityError` in its status);
  detection and tracking continue; other cameras are unaffected.

## 2. Pose model selection

| | YOLO11 pose (chosen) | RTMPose |
|---|---|---|
| Fits the existing stack | Same Ultralytics library, PyTorch 2.6, same device handling | Needs MMPose / MMCV or ONNX Runtime |
| Person boxes | Detects people and keypoints in one pass | Top-down: needs boxes from a detector, then one pass per person |
| Keypoints | COCO-17 | COCO-17 (also whole-body variants) |
| Cost on this CPU | ~100–120 ms per frame, whatever the number of people | Grows with the number of people |
| Licence | AGPL-3.0 (as for the detector) | Apache-2.0 |

`yolo11n-pose.pt` (6 MB) is the default; `yolo11s-pose.pt` is more accurate
and slower. Any model producing COCO-17 keypoints can replace it behind
`PoseEstimator` (`estimate(frame) -> list[PoseDetection]`). RTMPose is the
natural candidate if the AGPL licence is a problem or if accuracy on small,
far-away people becomes the bottleneck.

```powershell
python scripts/download_model.py --pose                    # models/pose/yolo11n-pose.pt
python scripts/download_model.py --pose yolo11s-pose.pt    # then CV_ACTIVITY_MODEL_PATH=models/pose/yolo11s-pose.pt
```

The service checks at load time that the file is a pose model with 17
keypoints (a detection model is refused).

## 3. Features

Keypoints are normalised against the **track's** box, in *box-height units*
(x and y both divided by the box height): a person far away and one close
by give the same numbers, and angles are not distorted. A keypoint counts as
visible only with confidence ≥ `CV_ACTIVITY_KEYPOINT_CONFIDENCE` and a
position near the box; everything else is missing, never interpolated.

| Feature | Meaning |
|---|---|
| `torso_angle` | Hip centre → shoulder centre, degrees from vertical |
| `shoulder_tilt`, `hip_tilt` | Alignment of the shoulders / hips, degrees from horizontal |
| `left_knee_angle`, `right_knee_angle` | Hip–knee–ankle angle (180 = straight) |
| `hip_angle` | Shoulder–hip–knee angle |
| `thigh_angle`, `thigh_ratio` | Thigh direction from vertical; thigh length / torso length (short = foreshortened, e.g. seated facing the camera) |
| `wrist_raise`, `wrist_reach`, `wrist_above_hip` | Hand position relative to the shoulder, in torso lengths |
| `head_offset` | Head below the shoulder line, in torso lengths |
| `orientation` | Shoulder width / torso length (facing the camera vs side-on) |
| `box_aspect`, `keypoint_confidence` | Box shape; mean confidence of visible keypoints |

Movement is temporal, not a frame feature: the classifier measures the speed
of the track's feet point (bottom-centre of its box) over the window, using
timestamps, in body heights per second.

A pose without a shoulder and a hip (no torso), or with fewer than 5 visible
keypoints, is not usable: that observation counts as "no pose".

## 4. Temporal classification

`pose-temporal-baseline` 1.0 (`BaselineTemporalClassifier`):

1. **Enough evidence?** Over the window (default 3 s): at least
   `CV_ACTIVITY_MIN_OBSERVATIONS` usable poses (default 4) spanning at least
   `CV_ACTIVITY_MIN_WINDOW_SECONDS` (default 1 s). Otherwise UNKNOWN
   (`NO_POSE`, `INSUFFICIENT_POSE`, `INSUFFICIENT_HISTORY`).
2. **Posture per frame**, each 0–1:
   * upright torso (< 20–40°) vs bent torso (> 30–55°, or head low);
   * seated: thigh near horizontal or strongly foreshortened, knees bent;
   * straight legs for standing (legs hidden → standing scores at most 0.45,
     so it ends up UNKNOWN rather than guessed);
   * reaching: a wrist above its shoulder, or a wrist far out horizontally
     above hip level.
3. **Window average**, weighted by keypoint confidence.
4. **Movement**: speed ≥ `CV_ACTIVITY_WALKING_SPEED` (0.3 body heights/s,
   ramped 0.6×–1.4×) of an upright, not-seated body is WALKING; it replaces
   STANDING.
5. **Decision** (shared with any future model), see section 5.

### A trained model later

`SequenceModelClassifier` loads a TorchScript module (LSTM, GRU or temporal
CNN) with `CV_ACTIVITY_CLASSIFIER=sequence` and
`CV_ACTIVITY_SEQUENCE_MODEL_PATH`. Input: `(1, T, 2F)` floats, the
`FEATURE_NAMES` vector per observation (NaN → 0) followed by a 0/1 "present"
mask; output: `(1, classes)` logits. Labels and the model's name / version
come from the file's extra files `labels.txt` (`STANDING,SITTING,…`) and
`meta.txt` (`name=…;version=…`). The same thresholds and UNKNOWN rules
apply. **No such model is trained or shipped**; without the file the service
logs a warning and uses the baseline.

## 5. Confidence and UNKNOWN

```text
confidence = best score × (0.5 + 0.5 × frame agreement) × (0.6 + 0.4 × share of looks with a usable pose)
```

* confidence < `CV_ACTIVITY_MIN_CONFIDENCE` (0.55) → UNKNOWN, `LOW_CONFIDENCE`;
* best − runner-up < `CV_ACTIVITY_CONFLICT_MARGIN` (0.15), or fewer than half
  the frames agree → UNKNOWN, `CONFLICTING`;
* the best guess is kept as `candidate` for review, never asserted.

Confidence is about the *prediction*. It is not a score of the person, and it
is kept apart from any business figure.

## 6. Activity state

`ActivityStateManager`, per `(camera, track)`:

* a different activity must be predicted continuously for
  `CV_ACTIVITY_MIN_STATE_SECONDS` (1.5 s, and at least twice) before the
  state changes;
* UNKNOWN must last `CV_ACTIVITY_UNKNOWN_AFTER_SECONDS` (3 s) before it
  replaces a known state (a short occlusion keeps the last activity):
  `SITTING → UNKNOWN → SITTING` only when the uncertainty really lasted;
* a stable state is re-confirmed every `CV_ACTIVITY_REPORT_SECONDS` (15 s);
* the confidence of a stable state is smoothed (exponential average);
* a track briefly lost keeps its state; one unseen for
  `CV_ACTIVITY_RESET_AFTER_SECONDS` (3 s) or reported lost is forgotten;
* the last 50 transitions are kept per track (`GET /cameras/{id}/activity`),
  and every transition is published as an event (the durable history).

## 7. Events

| Event | When | Notes |
|---|---|---|
| `ACTIVITY_STATE_CHANGED` | A new known activity was accepted | `payload.activity`, `previousActivity`, `previousDurationSeconds` |
| `ACTIVITY_UNCERTAIN` | The track's activity became UNKNOWN | `payload.activity` = `UNKNOWN`, `reason`, `candidate`; confidence optional; can never name a worker |
| `ACTIVITY_CLASSIFIED` | Periodic confirmation of a stable activity | at most every `CV_ACTIVITY_REPORT_SECONDS` per track |

Every activity event carries: `cameraId`, `trackId` (temporary), confidence,
`observationStart` / `observationEnd` (the window), `predictedAt`,
`stateSince`, `modelName` / `modelVersion` (the classifier), the pose model
and detector names, `sourceType: REAL`, `scores`, `quality` (observations,
usable poses, window length, keypoint confidence, legs visible, speed,
agreement), a stable `idempotencyKey` and a `cvEventId`. It never carries
keypoints, images or a worker id. The backend refuses anything outside the
catalog, garment-work labels from this service, UNKNOWN in a state change,
and any worker identity (see MONITORING_EVENTS.md).

## 8. Why pose estimation is not activity recognition

* **A pose is a skeleton, not an action.** The pose model says where the
  shoulders and knees are in one frame. "Sitting" or "walking" is an
  interpretation of how those points are arranged and how they move over
  time. Sewing, cutting or inspecting are interpretations of hands, tools and
  material, which a 17-point body skeleton barely sees.
* **Time is necessary.** One frame of a person mid-stride and one of a person
  standing can be the same skeleton; walking only exists as movement.
  Reaching, picking and placing are short sequences, not postures. Single
  frames are also noisy: a flicker in one keypoint would flip a single-frame
  classifier. Hence the window, the minimum durations and the UNKNOWN rules.
* **Camera angles change everything.** Pretrained pose models learn from
  eye-level photos. Factory cameras look down from the ceiling, at an angle,
  through machines. From above, a standing person's legs are foreshortened
  like a seated one's; side-on, one arm hides the other; a sewing machine
  hides the legs of everyone sitting at it. Rules or models tuned on one
  angle fail on another, so each camera position needs its own validation.
* **Garment work needs domain data.** Operators sit most of the day, their
  hands do the work, and the differences between operations (sewing,
  trimming, folding) are small hand and material movements. No public pose
  dataset contains them. Recognising them needs labelled clips from these
  factories, these machines and these angles, and probably models that look
  at the hands and the image, not only the skeleton.

## 9. Dataset: collection and annotation

Layout (created by `python scripts/activity_dataset.py init`; everything
under `data/` is ignored by Git, only the README and template are tracked):

```text
data/activity_dataset/
├── raw/          original clips, never modified
├── annotated/    clips with finished annotations (or exported annotation files)
├── processed/    derived data: pose / feature sequences per segment (.npz)
├── train/        training-split material (links or copies of processed data)
├── validation/
├── test/
├── reports/      evaluation reports (JSON)
└── annotations.csv
```

**Never commit footage, labels of real people or derived pose data.** Store
the dataset on a controlled factory drive with access limited to the people
working on it.

### Collecting clips

1. Get consent from the people filmed, and agreement from management and
   worker representatives, before recording. Explain the purpose (posture /
   movement model validation, not performance monitoring).
2. Record from the **real production camera positions** (the angles the
   model will see), across shifts, lighting, lines and machine types.
3. Short clips (10–60 s), each covering natural work, including people
   partly hidden by machines, entering / leaving and overlapping.
4. Give each recording an id (`rec-<line>-<camera>-<date>-<time>`) and each
   filmed person a pseudonymous **subject group** (`P017`): never their name
   or employee code in the dataset.

### Annotating

Annotate **time segments per person**: one row per (clip, person, activity
segment) in `annotations.csv` (template:
`data/activity_dataset/annotations.template.csv`). Columns: `clip_id`, `file`
(relative to the dataset folder), `split` (filled by the split tool),
`recording_id`, `subject_group`, `camera_angle`, `label`, `start_s`, `end_s`,
`roi_x` / `roi_y` / `roi_w` / `roi_h` (the person's box at `start_s`, 0–1
fractions of the frame, to pick the right track), `annotator`, `notes`.

| Label | Annotate when | Not when |
|---|---|---|
| `STANDING` | Upright on both feet, little horizontal displacement (shifting weight, turning on the spot, working at a table standing) | Walking a step or more per second; leaning on a stool (→ `SITTING` if the weight is on it) |
| `SITTING` | Body weight on a chair, stool or bench, whatever the hands do (sewing at a machine is `SITTING`) | Squatting / crouching (→ `BENDING`) |
| `WALKING` | Moving from one place to another, at least ~1 s of continuous steps | Two steps sideways at a workstation (→ `STANDING`) |
| `BENDING` | Torso clearly inclined forward (≳ 30°) or crouching, e.g. picking from the floor, low bins | A slight forward lean while sitting at a machine (→ `SITTING`) |
| `REACHING` | An arm raised above the shoulder or stretched far forward / sideways to take or place something | Hands moving in front of the body at a machine (→ the base posture) |
| `OTHER` | Anything else, or the label is ambiguous: keep it, it is skipped in scoring | — |

Rules: segments at least 1 s; boundaries at the moment the new activity is
clearly established; when unsure, use `OTHER` rather than guess; two
annotators on a sample of clips to measure agreement (if humans disagree,
the model can't be expected to agree).

## 10. Splitting without leakage

Adjacent frames of the same recording are nearly identical, and the same
person looks the same across recordings. If segments of one recording or one
person are in both training and test sets, the test score measures memory,
not generalisation.

* Split **by group, never by clip or segment**: `python scripts/activity_dataset.py split`
  joins recordings and subject groups into connected groups (two recordings
  sharing a person are one group) and assigns whole groups to train /
  validation / test (default 70 / 15 / 15, deterministic for a seed).
* `python scripts/activity_dataset.py check` reports leakage (a recording or
  a person in two splits), unknown labels, missing files and the class
  balance per split. `evaluate_activity.py` refuses to run on a leaking split.
* Better still for production claims: hold out a whole **camera position**
  or a whole **line** as the test set, to measure how the model handles an
  angle it has never seen.
* Tune thresholds on **validation**; run **test** once, at the end. Never
  report results on hand-picked demo frames.

## 11. Class imbalance

Garment operators sit most of the time: expect `SITTING` to dominate and
`REACHING` / `BENDING` to be rare. Then:

* accuracy is misleading (always answering SITTING scores high): report
  per-class precision / recall / F1 and the **macro** average;
* check the imbalance ratio per split (`activity_dataset.py check`,
  `groundTruthBalance` in the evaluation report);
* collect more of the rare classes deliberately rather than oversampling
  frames of the same few segments (oversampling duplicates leak);
* for training: class weights or balanced sampling *by segment*, and keep the
  test set's natural distribution.

## 12. Training a temporal model

Not done in this phase. When a validated dataset exists:

1. Run the pipeline over each annotated segment and save the per-observation
   `FEATURE_NAMES` vectors (and masks) into `processed/` (reuse
   `app/activity_recognition/offline.py`).
2. Train a small GRU / LSTM / temporal CNN on windows of the same length the
   service uses, on the training split only; select on validation.
3. Export with `torch.jit.script` / `trace`, with `labels.txt` and
   `meta.txt` extra files, into `models/activity/`.
4. Evaluate on the test split with `scripts/evaluate_activity.py`
   (`CV_ACTIVITY_CLASSIFIER=sequence`), compare against the baseline, keep
   the report next to the model, and only then enable it.

## 13. Evaluation

```powershell
python scripts/activity_dataset.py check
python scripts/evaluate_activity.py --split validation      # while tuning
python scripts/evaluate_activity.py --split test            # once, at the end
```

The script runs the real pipeline (detector, ByteTrack, pose model,
classifier, state manager) over each clip of the split, picks the annotated
person's track (by the ROI), skips the first second of each segment (the
window is filling), and compares each prediction with the label of that
moment. It reports, for the raw window prediction and the smoothed state:

* precision, recall, F1 per class, macro and weighted averages;
* the confusion matrix (rows = truth, with an UNKNOWN column);
* the UNKNOWN prediction rate (UNKNOWN counts against recall, and
  `accuracyWhenDecided` shows accuracy over non-UNKNOWN predictions);
* inference latency (pose, activity step, detection + tracking) and
  processing FPS;

and writes `reports/activity-eval-<split>-<time>.json`.

**Current status: pending.** Without labelled data the script prints
"Model evaluation is PENDING" and the workflow above. No accuracy is claimed
anywhere for this version.

## 14. Real-model integration tests with recorded videos

Unit tests mock the pose model (synthetic COCO-17 skeletons through the real
preprocessing, features, classifier, state manager, sessions and API).
Real-model tests run the actual weights:

```powershell
python scripts/download_model.py ; python scripts/download_model.py --pose
python scripts/make_sample_video.py
.venv\Scripts\python -m pytest -m model
```

They check that YOLO pose finds keypoints of the people in a real photo,
that a detection model is refused as a pose model, and that the full
pipeline on `people_walkthrough.mp4` produces activity events that pass the
contract for tracks the platform was told about. They are integration tests,
not accuracy tests (the sample is panned photographs).

To test with your own recording (with consent):

1. Put the clip in `data/` and run the benchmark:
   `python scripts/benchmark_activity.py data/my_clip.mp4 --process-fps 5 --activity-fps 2.5`
   (FPS, latency, predictions, state changes, nothing published).
2. Or end to end: `CV_ENABLE_PREVIEW=true`, `CV_ALLOW_SOURCE_OVERRIDE=true`,
   start a camera with `{"source": {"type": "file", "path": "my_clip.mp4"}, "activity": true}`,
   watch `GET /cameras/{id}/preview.jpg` (skeletons, labels, confidence) and
   `GET /cameras/{id}/activity`, and the dashboard's **Activity** page.
3. For numbers you can quote, annotate the clip and use `evaluate_activity.py`.

## 15. Limitations

* Rules, not a trained model; thresholds are reasoned defaults, not fitted to
  factory data. Evaluation pending.
* Fixed cameras assumed: camera motion (a pan) looks like walking.
* Seated people facing the camera, people seen from above, and people whose
  legs are hidden by machines are hard: expect UNKNOWN or STANDING / SITTING
  confusions there. Hidden legs deliberately lead to UNKNOWN.
* REACHING is only seen when the arm is clearly raised or stretched
  sideways / forwards in the image plane; reaching toward the camera is
  invisible to 2-D keypoints.
* Track id switches (crowding, occlusion) restart a person's window and
  state under the new id.
* Pose cost: ~100–120 ms per frame on the development CPU (one camera at
  5 fps detection + 2.5 fps pose stays real-time; 8 + 4 does not); a GPU is
  needed for several cameras with activity.
* No sewing, handling, machine-operation or picking classes (reserved in the
  contract, refused from this service); no identities; no productivity.
