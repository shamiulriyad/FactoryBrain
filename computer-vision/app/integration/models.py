"""Camera configuration as served by the backend (GET /api/integrations/cv/cameras)."""
from __future__ import annotations

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel

from app.zones.types import Zone


class _Camel(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, extra="ignore")


class ZonePoint(_Camel):
    x: float
    y: float


class ZoneConfig(_Camel):
    id: str
    name: str
    zone_type: str
    points: list[ZonePoint]
    workstation_id: str | None = None
    workstation_code: str | None = None
    line_id: str | None = None
    version: int = 1

    def to_zone(self) -> Zone:
        return Zone(self.id, self.name, self.zone_type, tuple((p.x, p.y) for p in self.points),
                    self.workstation_id, self.workstation_code, self.line_id)


class SourceConfig(_Camel):
    source_type: str = "None"
    # Contains credentials for RTSP / HTTP sources: never log it, never return it from this service's API.
    stream_url: str | None = Field(None, repr=False)
    webcam_label: str | None = None
    has_video: bool = False
    video_file_name: str | None = None
    video_content_type: str | None = None
    version: int = 0


class CameraConfig(_Camel):
    id: str
    code: str
    name: str
    camera_type: str
    factory_id: str
    factory_code: str
    line_id: str | None = None
    status: str
    source: SourceConfig
    zones: list[ZoneConfig] = Field(default_factory=list)
    covered_workstations: list[str] = Field(default_factory=list)
    frame_width: int | None = None
    frame_height: int | None = None

    @property
    def active(self) -> bool:
        return self.status == "Active"

    def zone_objects(self) -> list[Zone]:
        return [z.to_zone() for z in self.zones]
