import { cn } from "@/lib/cn";
import { LogoAppIcon, LogoSymbol, Wordmark, type LogoTone } from "./Logo";

type Props = {
  size?: number;
  className?: string;
  /** Show the mark on its navy app-icon tile, useful in headers. */
  framed?: boolean;
  /** Show the BunonBrain wordmark next to the mark. */
  withWordmark?: boolean;
  /** When true, draw the symbol's strokes on once on mount. */
  reveal?: boolean;
  /** Defaults to "auto" (follows the app theme); "onDark" on navy panels. */
  tone?: LogoTone;
};

/**
 * Brand mark for BunonBrain in the signed-in app, auth and onboarding pages:
 * the hanger-B symbol (optionally on its app-icon tile) with an optional
 * wordmark. See components/brand/Logo.tsx for the full logo family.
 */
export function BrandMark({ size = 28, className, framed = false, withWordmark = false, reveal = false, tone = "auto" }: Props) {
  const label = withWordmark ? undefined : "BunonBrain";
  return (
    <span className={cn("inline-flex items-center gap-2.5", className)}>
      {framed ? (
        <LogoAppIcon size={size} label={label} reveal={reveal} />
      ) : (
        <LogoSymbol size={size} tone={tone} label={label} reveal={reveal} />
      )}
      {withWordmark && <Wordmark height={Math.round(size * 0.48)} tone={tone} label="BunonBrain" />}
    </span>
  );
}

/** The bare symbol, kept for older call sites. */
export function BrandGlyph({ size = 28, className, reveal = false }: { size?: number; className?: string; reveal?: boolean }) {
  return <LogoSymbol size={size} className={className} reveal={reveal} />;
}
