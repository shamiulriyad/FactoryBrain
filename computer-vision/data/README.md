# Data (not committed)

* `samples/` – test videos made by `python scripts/make_sample_video.py` from the
  real photos that ship with Ultralytics (people walking through the frame, an
  occlusion, and a second camera). Use them for development; they are not
  factory footage.
* `cache/` – temporary working copies of a camera's recorded video, downloaded
  from the backend when its analysis starts and deleted when it stops.

To test with your own video, put an `.mp4` here (only with the consent of the
people in it) and either upload it to a camera in the platform (Cameras &
devices → camera → video source → recorded video), or, in development with
`CV_ALLOW_SOURCE_OVERRIDE=true`, start a camera with
`{"source": {"type": "file", "path": "my_video.mp4"}}`.

Nothing here is written by the service except the temporary cache: raw video
is never recorded.
