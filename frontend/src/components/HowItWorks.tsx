import { HOW_IT_WORKS } from "@/lib/tour";

export function HowItWorks() {
  return (
    <section id="how" aria-labelledby="how-title" className="fb-light relative z-20 border-t border-fb-line bg-fb-ink">
      <div className="mx-auto max-w-[1200px] px-4 py-24 md:px-8 md:py-32">
        <h2 id="how-title" className="font-display text-[2.6rem] font-extrabold leading-none md:text-[3.6rem]">
          {HOW_IT_WORKS.heading}
        </h2>
        <ol className="mt-12 grid gap-10 md:mt-16 md:grid-cols-3 md:gap-10">
          {HOW_IT_WORKS.steps.map((step, i) => (
            <li key={step.title} className="border-t border-fb-text/40 pt-5">
              <span className="font-plexmono text-[0.75rem] tracking-[0.14em] text-fb-muted">
                {String(i + 1).padStart(2, "0")}
              </span>
              <h3 className="mt-2 font-display text-[1.9rem] font-bold leading-tight">{step.title}</h3>
              <p className="mt-3 max-w-[34ch] leading-relaxed text-fb-text/90">{step.body}</p>
            </li>
          ))}
        </ol>
        <p className="mt-14 border-t border-fb-line pt-5 font-plexmono text-[0.82rem] leading-relaxed text-fb-muted md:mt-20">
          <span className="text-fb-text">Watches:</span> {HOW_IT_WORKS.watches}
          <span aria-hidden="true" className="mx-3 text-fb-text/40">
            /
          </span>
          <span className="text-fb-text">Does not watch:</span> {HOW_IT_WORKS.doesNotWatch}
        </p>
      </div>
    </section>
  );
}
