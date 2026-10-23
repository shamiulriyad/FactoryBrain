"use client";

import { cn } from "@/lib/cn";

export type AgentGlyph =
  | "line-throughput"
  | "maintenance"
  | "orchestrator";

export const AGENT_PALETTE: Record<AgentGlyph, { strong: string; soft: string; ring: string; label: string }> = {
  "line-throughput": {
    strong: "#7CD3F0",
    soft: "rgba(124, 211, 240, 0.18)",
    ring: "rgba(124, 211, 240, 0.4)",
    label: "Line Throughput"
  },
  maintenance: {
    strong: "#F0B97C",
    soft: "rgba(240, 185, 124, 0.18)",
    ring: "rgba(240, 185, 124, 0.4)",
    label: "Maintenance"
  },
  orchestrator: {
    strong: "#7CE0B8",
    soft: "rgba(124, 224, 184, 0.18)",
    ring: "rgba(124, 224, 184, 0.4)",
    label: "Orchestrator"
  }
};

/** Map agent id → glyph. */
export const AGENT_GLYPH: Record<string, AgentGlyph> = {
  "line-throughput-agent": "line-throughput",
  "maintenance-agent": "maintenance",
  "manager-agent": "orchestrator"
};

type Props = {
  agentId: string;
  size?: number;
  className?: string;
  framed?: boolean;
  /** Active highlight ring (used when the agent is selected from Ask). */
  active?: boolean;
};

export function AgentAvatar({ agentId, size = 22, className, framed = true, active = false }: Props) {
  const glyph = AGENT_GLYPH[agentId] ?? "orchestrator";
  const palette = AGENT_PALETTE[glyph];
  return (
    <span
      className={cn(
        "inline-flex items-center justify-center rounded-md",
        framed && "border",
        active && "ring-2 ring-offset-1 ring-offset-canvas",
        className
      )}
      style={{
        width: size + (framed ? 12 : 0),
        height: size + (framed ? 12 : 0),
        background: framed ? palette.soft : "transparent",
        borderColor: framed ? palette.ring : "transparent",
        // @ts-expect-error — CSS variable for active ring
        "--tw-ring-color": palette.strong
      }}
      aria-hidden
    >
      <GlyphSvg kind={glyph} size={size} color={palette.strong} />
    </span>
  );
}

function GlyphSvg({ kind, size, color }: { kind: AgentGlyph; size: number; color: string }) {
  const props = { width: size, height: size, viewBox: "0 0 24 24", "aria-hidden": true } as const;
  switch (kind) {
    case "line-throughput":
      // Gauge dial + arrow — line efficiency at a glance
      return (
        <svg {...props}>
          <path
            d="M4 16 a8 8 0 0 1 16 0"
            stroke={color}
            strokeWidth="1.6"
            strokeLinecap="round"
            fill="none"
            opacity="0.7"
          />
          <path d="M4 16 H7 M17 16 H20" stroke={color} strokeWidth="1.4" strokeLinecap="round" opacity="0.6" />
          <line x1="12" y1="16" x2="16" y2="9" stroke={color} strokeWidth="1.8" strokeLinecap="round" />
          <circle cx="12" cy="16" r="1.6" fill={color} />
        </svg>
      );
    case "maintenance":
      // Spanner + bearing ring
      return (
        <svg {...props}>
          <path
            d="M14.5 3 a5 5 0 0 1 5 5 a5 5 0 0 1-1 3 L21 14 l-3 3 -3-3 a5 5 0 0 1-5-5 a5 5 0 0 1 1-3 L9 3.5 L11 1.5 L13.5 4 Z"
            fill={color}
            opacity="0.92"
          />
          <circle cx="14.5" cy="8" r="1.8" fill="#04231C" opacity="0.7" />
          <path d="M4 18 L9 13 M4 21 L7 18" stroke={color} strokeWidth="1.5" strokeLinecap="round" />
        </svg>
      );
    case "orchestrator":
      // Three nodes + links — orchestrator fan-out
      return (
        <svg {...props}>
          <line x1="12" y1="5" x2="6" y2="17" stroke={color} strokeWidth="1.4" opacity="0.5" />
          <line x1="12" y1="5" x2="18" y2="17" stroke={color} strokeWidth="1.4" opacity="0.5" />
          <line x1="12" y1="5" x2="12" y2="14" stroke={color} strokeWidth="1.4" opacity="0.5" />
          <circle cx="12" cy="4" r="2.6" fill={color} />
          <circle cx="5" cy="18" r="2.2" fill={color} opacity="0.85" />
          <circle cx="12" cy="20" r="2.2" fill={color} opacity="0.85" />
          <circle cx="19" cy="18" r="2.2" fill={color} opacity="0.85" />
        </svg>
      );
  }
}
