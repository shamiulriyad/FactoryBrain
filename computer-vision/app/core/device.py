"""Chooses where inference runs: CUDA when present and asked for (or ``auto``), else CPU."""
from __future__ import annotations

import os
import platform
from dataclasses import asdict, dataclass


@dataclass(frozen=True)
class DeviceInfo:
    device: str                 # "cpu" or "cuda:0"
    requested: str
    cuda_available: bool
    gpu_name: str | None
    torch_version: str
    cpu_count: int
    platform: str
    note: str

    def as_dict(self) -> dict:
        return asdict(self)


def select_device(requested: str = "auto", cuda_available: bool | None = None) -> DeviceInfo:
    """``auto`` → first CUDA device if available, otherwise CPU. Asking for CUDA without a GPU falls back to CPU with a note."""
    import torch  # imported here so tests can patch availability cheaply

    has_cuda = torch.cuda.is_available() if cuda_available is None else cuda_available
    req = (requested or "auto").strip().lower()
    gpu = None
    if req == "cpu":
        device, note = "cpu", "CPU requested."
    elif req.startswith("cuda") or req == "auto":
        if has_cuda:
            device = "cuda:0" if req in ("auto", "cuda") else req
            try:
                gpu = torch.cuda.get_device_name(int(device.split(":")[1]))
            except Exception:  # pragma: no cover - depends on driver
                gpu = None
            note = "CUDA GPU in use."
        else:
            device = "cpu"
            note = "No CUDA GPU available: running on CPU." if req == "auto" else f"{requested} was requested but no CUDA GPU is available: falling back to CPU."
    else:
        device, note = "cpu", f"Unknown device '{requested}': using CPU."
    return DeviceInfo(device, requested, has_cuda, gpu, torch.__version__, os.cpu_count() or 1, platform.platform(), note)
