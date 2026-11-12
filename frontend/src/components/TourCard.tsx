import { FLOOR_STATUS, type FloorSection, type OverviewSection, type Pill, type TourSection } from "@/lib/tour";

const PILL_STYLE: Record<Pill, string> = {
  OK: "border-fb-ok/60 text-fb-ok",
  Watch: "border-fb-lamp/70 text-fb-lamp",
  Act: "border-fb-thread/80 text-fb-thread",
};

export function StatusPill({ pill }: { pill: Pill }) {
  return (
    <span
      className={`inline-flex items-center rounded-full border px-2 py-[3px] font-plexmono text-[0.68rem] uppercase leading-none tracking-[0.08em] ${PILL_STYLE[pill]}`}
    >
      {pill}
    </span>
  );
}

const cardBase =
  "pointer-events-auto w-full rounded-[4px] border border-fb-line bg-fb-card p-5 backdrop-blur-md md:max-w-[27rem] md:p-7";

function FloorCard({ section }: { section: FloorSection }) {
  const titleId = `tour-${section.step}-title`;
  return (
    <article className={cardBase} aria-labelledby={titleId}>
      <div className="flex items-start gap-4 md:gap-5">
        <span
          aria-hidden="true"
          className="font-display text-[3.6rem] font-extrabold leading-[0.8] text-fb-lamp md:text-[6rem]"
        >
          {section.floor}
        </span>
        <div className="min-w-0">
          <p className="font-plexmono text-[0.7rem] uppercase tracking-[0.14em] text-fb-muted">{section.label}</p>
          <h2 id={titleId} className="mt-2 font-display text-[1.6rem] font-bold leading-[1.02] md:text-[2.1rem]">
            {section.heading}
          </h2>
        </div>
      </div>
      <p className="mt-4 text-[0.92rem] leading-relaxed text-fb-text/90 md:text-[0.98rem]">{section.body}</p>
      <p
        className="mt-4 border-t border-fb-line pt-3 font-plexmono text-[0.76rem] leading-relaxed text-fb-text/90"
        data-example="true"
      >
        <span className="mr-2 uppercase tracking-[0.12em] text-fb-muted">Example reading</span>
        {section.reading.text}
      </p>
    </article>
  );
}

function OverviewCard({ section }: { section: OverviewSection }) {
  const titleId = `tour-${section.step}-title`;
  return (
    <article className={cardBase} aria-labelledby={titleId}>
      <p className="font-plexmono text-[0.7rem] uppercase tracking-[0.14em] text-fb-muted">{section.label}</p>
      <h2 id={titleId} className="mt-2 font-display text-[1.6rem] font-bold leading-[1.02] md:text-[2.1rem]">
        {section.heading}
      </h2>
      <p className="mt-3 hidden text-[0.95rem] leading-relaxed text-fb-text/90 sm:block">{section.body}</p>
      <ul className="mt-4" aria-label="Status by floor (example data)">
        {FLOOR_STATUS.map((s) => (
          <li
            key={s.floor}
            className="grid grid-cols-[1.6rem_minmax(0,1fr)_auto] items-center gap-3 border-t border-fb-line py-2 md:py-2.5"
            data-example="true"
          >
            <span className="font-plexmono text-[0.85rem] text-fb-muted">{s.floor}</span>
            <span className="min-w-0">
              <span className="block text-[0.88rem] font-medium leading-snug">{s.name}</span>
              <span className="block truncate text-[0.78rem] leading-snug text-fb-muted">{s.issue}</span>
            </span>
            <StatusPill pill={s.pill} />
          </li>
        ))}
      </ul>
    </article>
  );
}

export function TourCard({ section }: { section: TourSection }) {
  return section.kind === "floor" ? <FloorCard section={section} /> : <OverviewCard section={section} />;
}

/** One scroll section of the tour with its sticky card (left on desktop,
 *  pinned to the bottom of the screen on mobile). */
export function TourStep({ section, last }: { section: TourSection; last: boolean }) {
  return (
    <section
      id={`tour-${section.step}`}
      aria-labelledby={`tour-${section.step}-title`}
      className={`relative z-10 ${last ? "h-[170svh]" : "h-[150svh]"}`}
    >
      <div className="pointer-events-none sticky top-0 flex h-[100svh] items-end md:items-center">
        <div className="mx-auto w-full max-w-[1440px] px-4 pb-4 md:px-8 md:pb-0">
          <TourCard section={section} />
        </div>
      </div>
    </section>
  );
}
