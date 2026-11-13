"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useId, useRef, useState, type FormEvent } from "react";
import { CheckCircle2, Loader2 } from "lucide-react";
import { submitInquiry, type InquiryKind } from "@/lib/site/inquiry";
import { cn } from "@/lib/cn";

export interface ExtraField {
  key: string;
  label: string;
  type: "select" | "text";
  options?: { value: string; label: string }[];
  required?: boolean;
  placeholder?: string;
}

type StdField = "company" | "jobTitle" | "phone" | "subject";

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/;

const input =
  "mt-1.5 block min-h-12 w-full rounded-lg border bg-white px-3.5 text-[0.98rem] text-brand-text placeholder:text-brand-muted/70 transition-colors focus:border-brand-emerald focus:outline-none focus:ring-2 focus:ring-brand-emerald/25";

/**
 * Website form backed by POST /api/inquiries. Shows success only after the
 * API confirms it stored the message.
 */
export function InquiryForm({
  kind,
  fields = ["company"],
  extra = [],
  messageLabel = "How can we help?",
  messagePlaceholder,
  submitLabel = "Send message",
  subject,
  requirePhoneOrEmail = true,
}: {
  kind: InquiryKind;
  fields?: StdField[];
  extra?: ExtraField[];
  messageLabel?: string;
  messagePlaceholder?: string;
  submitLabel?: string;
  /** Fixed subject (hidden) for forms that don't ask for one. */
  subject?: string;
  requirePhoneOrEmail?: boolean;
}) {
  const id = useId();
  const pathname = usePathname() ?? "/";
  const formRef = useRef<HTMLFormElement>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [status, setStatus] = useState<{ kind: "idle" | "sending" | "error" | "sent"; message: string }>({ kind: "idle", message: "" });

  const onSubmit = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (status.kind === "sending") return;
    const data = new FormData(e.currentTarget);
    const get = (k: string) => String(data.get(k) ?? "").trim();
    const found: Record<string, string> = {};
    if (get("name").length < 2) found.name = "Enter your name.";
    const email = get("email");
    const phone = get("phone");
    if (email && !EMAIL.test(email)) found.email = "That doesn't look like an email address.";
    if (requirePhoneOrEmail && !email && !phone) found.email = fields.includes("phone") ? "Enter an email address or phone number." : "Enter your email address.";
    if (get("message").length < 10) found.message = "Tell us a little more (at least 10 characters).";
    for (const x of extra) if (x.required && !get(`x_${x.key}`)) found[`x_${x.key}`] = `Choose ${x.label.toLowerCase()}.`;
    setErrors(found);
    if (Object.keys(found).length) {
      setStatus({ kind: "error", message: Object.keys(found).length === 1 ? "One field needs attention." : `${Object.keys(found).length} fields need attention.` });
      formRef.current?.querySelector<HTMLElement>(`[name="${Object.keys(found)[0]}"]`)?.focus();
      return;
    }
    const details: Record<string, string> = {};
    for (const x of extra) if (get(`x_${x.key}`)) details[x.label] = x.options?.find((o) => o.value === get(`x_${x.key}`))?.label ?? get(`x_${x.key}`);

    setStatus({ kind: "sending", message: "Sending…" });
    const res = await submitInquiry({
      kind,
      name: get("name"),
      email: email || undefined,
      phone: phone || undefined,
      company: get("company") || undefined,
      jobTitle: get("jobTitle") || undefined,
      subject: subject ?? (get("subject") || undefined),
      message: get("message"),
      details,
      sourcePath: pathname,
      website: get("website") || undefined,
    });
    if (res.ok) {
      setStatus({ kind: "sent", message: "" });
      formRef.current?.reset();
      return;
    }
    setErrors(res.fieldErrors);
    setStatus({ kind: "error", message: res.message });
  };

  if (status.kind === "sent") {
    return (
      <div role="status" className="rounded-2xl border border-brand-emerald/30 bg-brand-emerald-light/50 p-8">
        <CheckCircle2 className="text-brand-emerald" size={28} aria-hidden />
        <h3 className="mt-4 font-heading text-[1.4rem] font-semibold text-brand-text">Message received</h3>
        <p className="mt-2 leading-relaxed text-brand-body">
          Your message has been saved and will be read by the BunonBrain team. We reply to the contact details you gave us.
        </p>
        <button
          type="button"
          onClick={() => setStatus({ kind: "idle", message: "" })}
          className="mt-6 inline-flex min-h-11 items-center rounded-lg border border-brand-border bg-white px-4 text-[0.93rem] font-medium text-brand-text hover:border-brand-navy/30"
        >
          Send another message
        </button>
      </div>
    );
  }

  const err = (name: string) =>
    errors[name] ? (
      <p id={`${id}-${name}-err`} className="mt-1.5 text-[0.85rem] text-brand-error">
        {errors[name]}
      </p>
    ) : null;
  const a11y = (name: string) => ({
    "aria-invalid": errors[name] ? true : undefined,
    "aria-describedby": errors[name] ? `${id}-${name}-err` : undefined,
  });
  const border = (name: string) => (errors[name] ? "border-brand-error" : "border-brand-border");
  const label = "block text-[0.88rem] font-medium text-brand-text";

  return (
    <form ref={formRef} onSubmit={onSubmit} noValidate className="relative grid gap-5 sm:grid-cols-2">
      <div>
        <label htmlFor={`${id}-name`} className={label}>
          Full name <span className="text-brand-error">*</span>
        </label>
        <input id={`${id}-name`} name="name" autoComplete="name" className={cn(input, border("name"))} {...a11y("name")} />
        {err("name")}
      </div>
      <div>
        <label htmlFor={`${id}-email`} className={label}>
          Work email {requirePhoneOrEmail && !fields.includes("phone") && <span className="text-brand-error">*</span>}
        </label>
        <input id={`${id}-email`} name="email" type="email" autoComplete="email" inputMode="email" className={cn(input, border("email"))} {...a11y("email")} />
        {err("email")}
      </div>
      {fields.includes("phone") && (
        <div>
          <label htmlFor={`${id}-phone`} className={label}>
            Phone <span className="font-normal text-brand-muted">(email or phone required)</span>
          </label>
          <input id={`${id}-phone`} name="phone" type="tel" autoComplete="tel" inputMode="tel" className={cn(input, border("phone"))} {...a11y("phone")} />
          {err("phone")}
        </div>
      )}
      {fields.includes("company") && (
        <div>
          <label htmlFor={`${id}-company`} className={label}>
            Company or factory
          </label>
          <input id={`${id}-company`} name="company" autoComplete="organization" className={cn(input, "border-brand-border")} />
        </div>
      )}
      {fields.includes("jobTitle") && (
        <div>
          <label htmlFor={`${id}-jobTitle`} className={label}>
            Job title
          </label>
          <input id={`${id}-jobTitle`} name="jobTitle" autoComplete="organization-title" className={cn(input, "border-brand-border")} />
        </div>
      )}
      {extra.map((x) => (
        <div key={x.key}>
          <label htmlFor={`${id}-x_${x.key}`} className={label}>
            {x.label} {x.required && <span className="text-brand-error">*</span>}
          </label>
          {x.type === "select" ? (
            <select id={`${id}-x_${x.key}`} name={`x_${x.key}`} defaultValue="" className={cn(input, border(`x_${x.key}`))} {...a11y(`x_${x.key}`)}>
              <option value="" disabled>
                Choose…
              </option>
              {x.options?.map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </select>
          ) : (
            <input id={`${id}-x_${x.key}`} name={`x_${x.key}`} placeholder={x.placeholder} className={cn(input, border(`x_${x.key}`))} {...a11y(`x_${x.key}`)} />
          )}
          {err(`x_${x.key}`)}
        </div>
      ))}
      {fields.includes("subject") && (
        <div className="sm:col-span-2">
          <label htmlFor={`${id}-subject`} className={label}>
            Subject
          </label>
          <input id={`${id}-subject`} name="subject" className={cn(input, "border-brand-border")} />
        </div>
      )}
      <div className="sm:col-span-2">
        <label htmlFor={`${id}-message`} className={label}>
          {messageLabel} <span className="text-brand-error">*</span>
        </label>
        <textarea
          id={`${id}-message`}
          name="message"
          rows={5}
          maxLength={4000}
          placeholder={messagePlaceholder}
          className={cn(input, "min-h-[140px] py-3", border("message"))}
          {...a11y("message")}
        />
        {err("message")}
      </div>
      {/* Honeypot: invisible to people and screen readers. */}
      <div aria-hidden="true" className="absolute -left-[9999px] h-px w-px overflow-hidden">
        <label htmlFor={`${id}-website`}>Website</label>
        <input id={`${id}-website`} name="website" tabIndex={-1} autoComplete="off" />
      </div>
      <div className="flex flex-col gap-4 sm:col-span-2 sm:flex-row sm:items-center sm:justify-between">
        <p className="text-[0.84rem] leading-relaxed text-brand-muted">
          We use these details only to reply. See our{" "}
          <Link href="/legal/privacy-policy" className="text-brand-emerald-ink underline underline-offset-2">
            privacy policy
          </Link>
          .
        </p>
        <button
          type="submit"
          disabled={status.kind === "sending"}
          className="inline-flex min-h-12 shrink-0 items-center justify-center gap-2 rounded-lg bg-brand-emerald-ink px-6 font-medium text-white hover:bg-[rgb(var(--brand-emerald-ink-hover))] disabled:opacity-70"
        >
          {status.kind === "sending" && <Loader2 size={16} className="animate-spin" aria-hidden />}
          {status.kind === "sending" ? "Sending…" : submitLabel}
        </button>
      </div>
      <p role="status" aria-live="polite" className={cn("text-[0.9rem] sm:col-span-2", status.kind === "error" ? "text-brand-error" : "text-brand-muted")}>
        {status.kind === "error" ? status.message : ""}
      </p>
    </form>
  );
}
