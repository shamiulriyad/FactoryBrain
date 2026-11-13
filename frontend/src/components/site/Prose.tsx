import { AlertTriangle, Info } from "lucide-react";
import { slugify, type Block } from "@/content/site/blocks";
import { cn } from "@/lib/cn";

/**
 * Renders structured blocks. Text supports **bold** and [TBC: …] markers;
 * the latter highlight wording that still needs company or legal confirmation.
 */
function Inline({ text }: { text: string }) {
  const parts = text.split(/(\*\*[^*]+\*\*|\[TBC:[^\]]+\])/g);
  return (
    <>
      {parts.map((p, i) => {
        if (p.startsWith("**") && p.endsWith("**")) return <strong key={i}>{p.slice(2, -2)}</strong>;
        if (p.startsWith("[TBC:")) {
          return (
            <span key={i} className="site-tbc" title="To be confirmed by the company or its legal advisers">
              {p.slice(5, -1).trim()}
            </span>
          );
        }
        return <span key={i}>{p}</span>;
      })}
    </>
  );
}

export function Prose({ blocks, className }: { blocks: Block[]; className?: string }) {
  return (
    <div className={cn("site-prose", className)}>
      {blocks.map((b, i) => {
        switch (b.t) {
          case "p":
            return (
              <p key={i}>
                <Inline text={b.text} />
              </p>
            );
          case "h2":
            return (
              <h2 key={i} id={b.id ?? slugify(b.text)}>
                {b.text}
              </h2>
            );
          case "h3":
            return <h3 key={i}>{b.text}</h3>;
          case "ul":
            return (
              <ul key={i}>
                {b.items.map((x, j) => (
                  <li key={j}>
                    <Inline text={x} />
                  </li>
                ))}
              </ul>
            );
          case "ol":
            return (
              <ol key={i}>
                {b.items.map((x, j) => (
                  <li key={j}>
                    <Inline text={x} />
                  </li>
                ))}
              </ol>
            );
          case "formula":
            return (
              <figure key={i} className="my-6 rounded-xl border border-brand-border bg-brand-offwhite px-5 py-4">
                <p className="font-plexmono text-[0.95rem] leading-relaxed text-brand-text">{b.text}</p>
                {b.caption && <figcaption className="mt-1 text-[0.85rem] text-brand-muted">{b.caption}</figcaption>}
              </figure>
            );
          case "note":
            return (
              <div
                key={i}
                className={cn(
                  "my-6 flex gap-3 rounded-xl border px-5 py-4 text-[0.95rem]",
                  b.tone === "draft" ? "border-brand-warning/40 bg-brand-warning/10" : "border-brand-info/25 bg-brand-info/5",
                )}
              >
                {b.tone === "draft" ? (
                  <AlertTriangle size={18} aria-hidden className="mt-0.5 shrink-0 text-[rgb(var(--brand-warning-ink))]" />
                ) : (
                  <Info size={18} aria-hidden className="mt-0.5 shrink-0 text-brand-info" />
                )}
                <p className="m-0">
                  <Inline text={b.text} />
                </p>
              </div>
            );
          case "table":
            return (
              <div key={i} className="overflow-x-auto">
                <table>
                  <thead>
                    <tr>
                      {b.head.map((h) => (
                        <th key={h} scope="col">
                          {h}
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {b.rows.map((r, j) => (
                      <tr key={j}>
                        {r.map((c, k) => (
                          <td key={k}>
                            <Inline text={c} />
                          </td>
                        ))}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            );
        }
      })}
    </div>
  );
}

/** Table of contents from the h2 blocks. */
export function Toc({ blocks, title = "On this page" }: { blocks: Block[]; title?: string }) {
  const heads = blocks.filter((b): b is Extract<Block, { t: "h2" }> => b.t === "h2");
  if (heads.length < 2) return null;
  return (
    <nav aria-label={title} className="text-[0.9rem]">
      <p className="font-plexmono text-[0.7rem] uppercase tracking-[0.14em] text-brand-muted">{title}</p>
      <ol className="mt-3 space-y-1 border-l border-brand-border">
        {heads.map((h) => (
          <li key={h.text}>
            <a href={`#${h.id ?? slugify(h.text)}`} className="-ml-px block border-l border-transparent py-1 pl-4 text-brand-body hover:border-brand-emerald hover:text-brand-text">
              {h.text}
            </a>
          </li>
        ))}
      </ol>
    </nav>
  );
}
