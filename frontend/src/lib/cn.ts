import { type ClassValue, clsx } from "clsx";
import { extendTailwindMerge } from "tailwind-merge";

/**
 * tailwind-merge that knows this project's custom font sizes
 * (tailwind.config.ts → fontSize: display, title, body, caption).
 * Without this it reads `text-body` as a text *colour* and drops a real
 * colour class such as `text-[var(--btn-primary-fg)]` as a "conflict".
 */
const twMerge = extendTailwindMerge({
  extend: {
    classGroups: {
      "font-size": [{ text: ["display", "title", "body", "caption"] }]
    }
  }
});

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}
