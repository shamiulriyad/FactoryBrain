# BunonBrain computer-vision service (CPU image).
# Build from this folder:   docker build -t bunonbrain-cv .
# Run (weights mounted, settings from .env):
#   docker run --rm -p 8100:8100 --env-file .env -e CV_HOST=0.0.0.0 \
#     -e CV_BACKEND_URL=http://host.docker.internal:5000 -v "$PWD/models:/app/models:ro" bunonbrain-cv
# A GPU image needs a CUDA base image and the CUDA PyTorch wheels (see README).
FROM python:3.11-slim

ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1
# OpenCV runtime libraries (video decoding through FFmpeg is bundled in the wheel).
RUN apt-get update && apt-get install -y --no-install-recommends libgl1 libglib2.0-0 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY requirements.txt .
RUN pip install torch==2.6.0 torchvision==0.21.0 --index-url https://download.pytorch.org/whl/cpu \
 && pip install -r requirements.txt

COPY app ./app
COPY scripts ./scripts
RUN useradd --create-home --uid 10001 cv && mkdir -p models data && chown -R cv /app
USER cv

EXPOSE 8100
HEALTHCHECK --interval=30s --timeout=5s CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8100/health')"
CMD ["sh", "-c", "uvicorn app.main:create_app --factory --host ${CV_HOST:-0.0.0.0} --port ${CV_PORT:-8100}"]
