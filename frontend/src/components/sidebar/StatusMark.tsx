import { cn } from "@/lib/cn";
import type { FloorStatus } from "@/lib/useFloorStatus";

/* Shape AND colour, so status never relies on colour alone:
   OK = circle, Watch = diamond, Act = square. */
const SHAPE: Record<FloorStatus, string> = {
  ok: "rounded-full bg-sb-ok",
  watch: "rotate-45 rounded-[1px] bg-sb-watch",
  act: "rounded-[1px] bg-sb-act",
};

const WORD_COLOR: Record<FloorStatus, string> = {
  ok: "text-sb-ok",
  watch: "text-sb-watch",
  act: "text-sb-act",
};

/** Just the shape. `ring` draws a 2px ring in the sidebar background colour. */
export function StatusShape({
  status,
  size = 8,
  ring = false,
  className,
}: {
  status: FloorStatus;
  size?: number;
  ring?: boolean;
  className?: string;
}) {
  // A rotated square looks larger than a circle of the same box; shrink it a touch.
  const px = status === "watch" ? Math.round(size * 0.82) : size;
  return (
    <span
      aria-hidden="true"
      className={cn("inline-block shrink-0", SHAPE[status], ring && "shadow-[0_0_0_2px_#0d1218]", className)}
      style={{ width: px, height: px }}
    />
  );
}

/** Shape followed by the status word, for the expanded sidebar. */
export function StatusMark({ status, label }: { status: FloorStatus; label: string }) {
  return (
    <span className="inline-flex shrink-0 items-center gap-1.5" aria-hidden="true">
      <StatusShape status={status} />
      <span className={cn("font-plexmono text-[11px] font-medium uppercase leading-none tracking-[0.06em]", WORD_COLOR[status])}>
        {label}
      </span>
    </span>
  );
}
