/**
 * "Book a demo" form: validation and submission. Wire submitDemoRequest to
 * the real endpoint later; the form only depends on its signature.
 */

export interface DemoRequest {
  name: string;
  factory: string;
  contact: string;
  lines: string;
}

export type DemoField = keyof DemoRequest;
export type DemoErrors = Partial<Record<DemoField, string>>;

export const LINE_OPTIONS = [
  { value: "1-5", label: "1 to 5 lines" },
  { value: "6-15", label: "6 to 15 lines" },
  { value: "16-30", label: "16 to 30 lines" },
  { value: "31-60", label: "31 to 60 lines" },
  { value: "60+", label: "More than 60 lines" },
];

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/;
const PHONE = /^\+?[\d\s\-().]{7,20}$/;

export function validateDemoRequest(data: DemoRequest): DemoErrors {
  const errors: DemoErrors = {};
  if (data.name.trim().length < 2) errors.name = "Enter your name.";
  if (data.factory.trim().length < 2) errors.factory = "Enter your factory or company name.";
  const contact = data.contact.trim();
  const digits = contact.replace(/\D/g, "");
  if (!contact) errors.contact = "Enter an email address or phone number.";
  else if (!EMAIL.test(contact) && !(PHONE.test(contact) && digits.length >= 7))
    errors.contact = "That doesn't look like an email address or phone number.";
  if (!LINE_OPTIONS.some((o) => o.value === data.lines))
    errors.lines = "Choose how many sewing lines you run.";
  return errors;
}

export interface DemoResult {
  ok: boolean;
  message: string;
}

/**
 * Sends a demo request. Currently a stand-in that resolves after a short
 * delay; replace the body with a fetch to the real API.
 */
export async function submitDemoRequest(data: DemoRequest): Promise<DemoResult> {
  const errors = validateDemoRequest(data);
  if (Object.keys(errors).length > 0) {
    return { ok: false, message: "Please fix the highlighted fields." };
  }
  // TODO: POST to the demo-request API, e.g.
  // const res = await fetch("/api/demo-requests", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(data) });
  // if (!res.ok) return { ok: false, message: "We couldn't send your request. Please try again." };
  await new Promise((r) => setTimeout(r, 600));
  return {
    ok: true,
    message: `Thanks, ${data.name.trim().split(/\s+/)[0]}. We'll be in touch to arrange a time.`,
  };
}
