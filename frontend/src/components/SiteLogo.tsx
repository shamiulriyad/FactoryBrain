/** BunonBrain mark: a building section with one lit (emerald) floor. Outline follows the text colour. */
export function LogoMark({ size = 26 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 26 26" aria-hidden="true" focusable="false">
      <rect x="3.5" y="2.5" width="19" height="21" rx="1.5" fill="none" stroke="currentColor" strokeWidth="1.6" />
      <path d="M3.5 8.2h19M3.5 13h19M3.5 17.8h19" stroke="currentColor" strokeWidth="1.2" opacity="0.5" />
      <rect x="5.2" y="9.4" width="15.6" height="2.4" style={{ fill: "rgb(var(--brand-emerald))" }} />
      <circle cx="16.5" cy="10.6" r="1.5" style={{ fill: "rgb(var(--brand-emerald-light))" }} />
    </svg>
  );
}
