"""Worker activity recognition, first version: observable posture and movement of tracked people.

    tracked person (ByteTrack) → pose (YOLO pose) → normalised keypoints → pose features
    → temporal buffer per (camera, track) → temporal classifier → activity state → events

It predicts STANDING, SITTING, WALKING, BENDING or REACHING, or UNKNOWN when
the evidence is not good enough. It does not recognise sewing or any other
garment operation, does not identify anyone and does not measure productivity.
"""
