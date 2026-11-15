# Trained activity models

Empty on purpose: no activity model has been trained yet. The service uses the
rule-based `pose-temporal-baseline` classifier.

A future trained temporal model (LSTM / GRU / temporal CNN) goes here as a
TorchScript file, used with `CV_ACTIVITY_CLASSIFIER=sequence` and
`CV_ACTIVITY_SEQUENCE_MODEL_PATH`. Its input format, metadata and the
training / evaluation requirements are in docs/ACTIVITY_RECOGNITION.md.
Never put a model here without its evaluation report on a held-out test set.
