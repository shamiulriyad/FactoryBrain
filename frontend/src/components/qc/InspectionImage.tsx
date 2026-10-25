"use client";

// The stored inspection image with the provider's localisation drawn on top:
// boxes and, when the provider returned one, the mask outline. The image is
// fetched with the bearer token (an <img src> can't carry it) and shown from
// an object URL. In drawing mode a drag on the image produces a normalised
// box for an inspector-added defect.

import { useEffect, useRef, useState } from "react";
import { ImageOff } from "lucide-react";
import { Skeleton } from "@/components/ui/Skeleton";
import { Button } from "@/components/ui/Button";
import { errorMessage, isAbort } from "@/lib/api";
import { boxStyle, findingLabel, formatConfidence, polygonPoints } from "@/lib/qcInspection";
import { fetchInspectionImage } from "@/services/qc.api";
import { cn } from "@/lib/cn";
import type { QcBox, QcFinding } from "@/services/contracts";

type Overlay = { id: string; box?: QcBox | null; polygon?: number[][] | null; label: string; tone: "model" | "inspector" | "rejected" | "draft" };

export function overlaysFor(findings: QcFinding[]): Overlay[] {
  return findings
    .filter((f) => f.box || (f.polygon && f.polygon.length >= 3))
    .map((f) => ({
      id: f.id,
      box: f.box,
      polygon: f.polygon,
      label: `${findingLabel(f)}${f.source === "model" ? ` · ${formatConfidence(f.confidence)}` : ""}`,
      tone: f.decision === "rejected" ? "rejected" : f.source === "inspector" ? "inspector" : "model"
    }));
}

const TONE: Record<Overlay["tone"], string> = {
  model: "border-[var(--risk-high)] bg-[var(--risk-high-soft)]",
  inspector: "border-[var(--info)] bg-[var(--info-soft)]",
  rejected: "border-dashed border-fg-tertiary bg-transparent opacity-60",
  draft: "border-dashed border-[var(--accent)] bg-[var(--accent-soft)]"
};
const STROKE: Record<Overlay["tone"], string> = {
  model: "var(--risk-high)",
  inspector: "var(--info)",
  rejected: "var(--fg-tertiary)",
  draft: "var(--accent)"
};

type Props = {
  url: string;
  alt: string;
  width: number;
  height: number;
  overlays: Overlay[];
  highlightedId?: string | null;
  onSelect?: (id: string) => void;
  drawing?: boolean;
  onDraw?: (box: QcBox) => void;
};

export function InspectionImage({ url, alt, width, height, overlays, highlightedId, onSelect, drawing, onDraw }: Props) {
  const [src, setSrc] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [drag, setDrag] = useState<{ x0: number; y0: number; x1: number; y1: number } | null>(null);
  const frame = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const ctrl = new AbortController();
    let objectUrl: string | null = null;
    setError(null);
    setSrc(null);
    fetchInspectionImage(url, ctrl.signal)
      .then((u) => {
        objectUrl = u;
        setSrc(u);
      })
      .catch((e) => {
        if (!isAbort(e)) setError(errorMessage(e));
      });
    return () => {
      ctrl.abort();
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [url, attempt]);

  function point(e: React.PointerEvent): { x: number; y: number } {
    const r = frame.current!.getBoundingClientRect();
    return { x: Math.min(1, Math.max(0, (e.clientX - r.left) / r.width)), y: Math.min(1, Math.max(0, (e.clientY - r.top) / r.height)) };
  }

  const draft: Overlay | null = drag
    ? {
        id: "draft",
        label: "New defect",
        tone: "draft",
        box: { x: Math.min(drag.x0, drag.x1), y: Math.min(drag.y0, drag.y1), width: Math.abs(drag.x1 - drag.x0), height: Math.abs(drag.y1 - drag.y0) }
      }
    : null;
  const shown = draft ? [...overlays, draft] : overlays;

  if (error) {
    return (
      <div className="surface-2 flex flex-col items-center justify-center gap-2 py-10 text-center" role="alert" data-testid="qc-image-error">
        <ImageOff size={20} className="text-fg-tertiary" aria-hidden />
        <p className="text-caption text-fg-secondary">The image could not be loaded: {error}</p>
        <Button size="sm" variant="secondary" onClick={() => setAttempt((n) => n + 1)}>Retry</Button>
      </div>
    );
  }

  return (
    <div
      ref={frame}
      className={cn("relative w-full overflow-hidden rounded-[var(--radius-control)] border border-border-subtle bg-surface-2 select-none", drawing && "cursor-crosshair touch-none")}
      style={{ aspectRatio: `${width} / ${height}` }}
      data-testid="qc-image"
      onPointerDown={(e) => {
        if (!drawing) return;
        const p = point(e);
        (e.target as Element).setPointerCapture?.(e.pointerId);
        setDrag({ x0: p.x, y0: p.y, x1: p.x, y1: p.y });
      }}
      onPointerMove={(e) => {
        if (!drawing || !drag) return;
        const p = point(e);
        setDrag({ ...drag, x1: p.x, y1: p.y });
      }}
      onPointerUp={() => {
        if (!drawing || !drag || !draft?.box) return;
        setDrag(null);
        if (draft.box.width > 0.01 && draft.box.height > 0.01) onDraw?.(draft.box);
      }}
    >
      {src ? (
        // eslint-disable-next-line @next/next/no-img-element -- object URL of an authenticated download
        <img src={src} alt={alt} className="absolute inset-0 h-full w-full object-contain" draggable={false} />
      ) : (
        <Skeleton className="absolute inset-0" />
      )}
      <svg className="pointer-events-none absolute inset-0 h-full w-full" viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden>
        {shown
          .filter((o) => o.polygon && o.polygon.length >= 3)
          .map((o) => (
            <polygon key={`p-${o.id}`} points={polygonPoints(o.polygon!)} fill="none" stroke={STROKE[o.tone]} strokeWidth={0.5} vectorEffect="non-scaling-stroke" />
          ))}
      </svg>
      {shown
        .filter((o) => o.box)
        .map((o) => (
          <button
            key={o.id}
            type="button"
            disabled={!onSelect || o.id === "draft" || drawing}
            onClick={() => onSelect?.(o.id)}
            data-testid="qc-overlay-box"
            data-finding-id={o.id}
            aria-label={`Defect: ${o.label}`}
            className={cn(
              "absolute border-2 rounded-sm transition-shadow disabled:cursor-default",
              TONE[o.tone],
              (drawing || o.id === "draft") && "pointer-events-none",
              highlightedId === o.id && "ring-2 ring-offset-1 ring-[var(--accent)]"
            )}
            style={boxStyle(o.box!)}
          >
            <span className="absolute -top-5 left-0 max-w-[16rem] truncate whitespace-nowrap rounded-sm bg-[var(--bg-surface)]/90 px-1 text-[11px] leading-4 text-fg-primary shadow-sm">
              {o.label}
            </span>
          </button>
        ))}
    </div>
  );
}
