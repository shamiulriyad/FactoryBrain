"""Garment-specific activity recognition: dataset preparation, training, evaluation and live inference.

    recorded video → import → clips → annotations → features (person crop CNN embedding + pose + motion)
    → leakage-safe split → train (GRU / temporal CNN head) → evaluate on a held-out test set
    → live: existing detection, ByteTrack and pose stage → per-track feature buffer → model
      → confidence / uncertainty → state smoothing → GARMENT_ACTIVITY_* events

It reuses the person detector, tracker, pose model, keypoint preprocessing and
state manager of the service; it never creates track ids. Its predictions are
a separate event family next to the generic posture recognition, which stays
available. No trained garment model ships with the service: accuracy on
garment-factory footage is PENDING until one is trained and evaluated on
labelled factory clips.
"""
