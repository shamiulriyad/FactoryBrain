import type { ReactNode } from "react";
import { CheckList, Container, PageHero, type Crumb } from "./ui";

/** Layout for pages whose main job is a form: hero, then the form beside a short explainer. */
export function FormPage({
  crumbs,
  eyebrow,
  title,
  lead,
  asideTitle,
  points,
  asideFooter,
  formTitle,
  children,
}: {
  crumbs: Crumb[];
  eyebrow: string;
  title: string;
  lead: string;
  asideTitle: string;
  points: ReactNode[];
  asideFooter?: ReactNode;
  formTitle: string;
  children: ReactNode;
}) {
  return (
    <>
      <PageHero crumbs={crumbs} eyebrow={eyebrow} title={title} lead={lead} />
      <section aria-labelledby="form-title" className="bg-white py-16 md:py-20">
        <Container wide className="grid gap-12 lg:grid-cols-[minmax(0,0.75fr)_minmax(0,1.25fr)]">
          <aside className="lg:pt-2">
            <h2 className="font-heading text-[1.35rem] font-semibold text-brand-text">{asideTitle}</h2>
            <CheckList className="mt-6" items={points} />
            {asideFooter && <div className="mt-10 border-t border-brand-border pt-6 text-[0.93rem] leading-relaxed text-brand-body">{asideFooter}</div>}
          </aside>
          <div className="rounded-3xl border border-brand-border bg-white p-6 shadow-[0_24px_60px_-30px_rgba(16,38,61,0.25)] md:p-10">
            <h2 id="form-title" className="mb-8 font-heading text-[1.5rem] font-semibold text-brand-text">
              {formTitle}
            </h2>
            {children}
          </div>
        </Container>
      </section>
    </>
  );
}
