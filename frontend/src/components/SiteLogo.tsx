import { LogoSymbol, type LogoTone } from "@/components/brand/Logo";

/** BunonBrain symbol (hanger B with fabric waves) for the marketing site. */
export function LogoMark({ size = 26, tone = "color" }: { size?: number; tone?: LogoTone }) {
  return <LogoSymbol size={size} tone={tone} />;
}
