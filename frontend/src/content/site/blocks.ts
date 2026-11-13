/** Structured long-form content, rendered by components/site/Prose.tsx. */
export type Block =
  | { t: "p"; text: string }
  | { t: "h2"; text: string; id?: string }
  | { t: "h3"; text: string }
  | { t: "ul"; items: string[] }
  | { t: "ol"; items: string[] }
  | { t: "formula"; text: string; caption?: string }
  | { t: "note"; text: string; tone?: "info" | "draft" }
  | { t: "table"; head: string[]; rows: string[][] };

export const slugify = (s: string) =>
  s
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-|-$/g, "");
