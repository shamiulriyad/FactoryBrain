import { cn } from "@/lib/cn";
import type { FloorStatus } from "@/lib/useFloorStatus";
import { StatusShape } from "./StatusMark";

/**
 * A floor code in a 28px round "lift button". In the collapsed sidebar the
 * floor's status shows as a 7px marker on its top-right corner.
 */
export function FloorKey({
  code,
  active,
  status,
  showDot,
}: {
  code: string;
  active: boolean;
  status?: FloorStatus;
  showDot?: boolean;
}) {
  return (
    <span
      aria-hidden="true"
      className={cn(
        "relative inline-flex size-7 shrink-0 items-center justify-center rounded-full border font-display text-[15px] font-bold leading-none",
        active ? "border-sb-accent bg-sb-accent text-sb-ink" : "border-[#3a4452] bg-sb-input text-sb-text",
      )}
    >
      {code}
      {showDot && status && <StatusShape status={status} size={7} ring className="absolute -right-0.5 -top-0.5" />}
    </span>
  );
}
