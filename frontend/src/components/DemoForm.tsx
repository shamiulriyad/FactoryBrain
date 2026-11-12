"use client";

import { useRef, useState, type ChangeEvent, type FormEvent } from "react";
import {
  LINE_OPTIONS,
  submitDemoRequest,
  validateDemoRequest,
  type DemoErrors,
  type DemoField,
  type DemoRequest,
} from "@/lib/demoRequest";

type Status = { kind: "idle" | "error" | "sending" | "sent"; message: string };

const EMPTY: DemoRequest = { name: "", factory: "", contact: "", lines: "" };
const ORDER: DemoField[] = ["name", "factory", "contact", "lines"];

const inputClass =
  "mt-2 block min-h-12 w-full rounded-[3px] border bg-[#151c25] px-3 text-[1rem] text-fb-text placeholder:text-fb-muted/80 focus:outline-none focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-fb-lamp";

export function DemoForm() {
  const [values, setValues] = useState<DemoRequest>(EMPTY);
  const [errors, setErrors] = useState<DemoErrors>({});
  const [status, setStatus] = useState<Status>({ kind: "idle", message: "" });
  const formRef = useRef<HTMLFormElement>(null);

  const onChange = (field: DemoField) => (e: ChangeEvent<HTMLInputElement | HTMLSelectElement>) => {
    const next = { ...values, [field]: e.target.value };
    setValues(next);
    // Clear a field's message as soon as it becomes valid.
    if (errors[field] && !validateDemoRequest(next)[field]) {
      setErrors((prev) => ({ ...prev, [field]: undefined }));
    }
  };

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (status.kind === "sending") return;
    const found = validateDemoRequest(values);
    setErrors(found);
    const invalid = ORDER.filter((f) => found[f]);
    if (invalid.length > 0) {
      setStatus({
        kind: "error",
        message: invalid.length === 1 ? "One field needs attention." : `${invalid.length} fields need attention.`,
      });
      formRef.current?.querySelector<HTMLElement>(`[name="${invalid[0]}"]`)?.focus();
      return;
    }
    setStatus({ kind: "sending", message: "Sending your request…" });
    try {
      const res = await submitDemoRequest(values);
      setStatus({ kind: res.ok ? "sent" : "error", message: res.message });
      if (res.ok) setValues(EMPTY);
    } catch {
      setStatus({ kind: "error", message: "We couldn't send your request. Please try again." });
    }
  };

  const field = (name: DemoField) => ({
    id: `demo-${name}`,
    name,
    value: values[name],
    onChange: onChange(name),
    "aria-invalid": errors[name] ? true : undefined,
    "aria-describedby": errors[name] ? `demo-${name}-error` : undefined,
    className: `${inputClass} ${errors[name] ? "border-fb-thread" : "border-fb-line"}`,
  });

  const error = (name: DemoField) =>
    errors[name] ? (
      <p id={`demo-${name}-error`} className="mt-1.5 text-[0.85rem] text-fb-thread">
        {errors[name]}
      </p>
    ) : null;

  const statusColor =
    status.kind === "error" ? "text-fb-thread" : status.kind === "sent" ? "text-fb-ok" : "text-fb-muted";

  return (
    <form ref={formRef} noValidate onSubmit={onSubmit} className="grid gap-5" aria-describedby="demo-status">
      <div>
        <label htmlFor="demo-name" className="text-[0.9rem] font-medium">
          Your name
        </label>
        <input type="text" autoComplete="name" required {...field("name")} />
        {error("name")}
      </div>
      <div>
        <label htmlFor="demo-factory" className="text-[0.9rem] font-medium">
          Factory
        </label>
        <input type="text" autoComplete="organization" required {...field("factory")} />
        {error("factory")}
      </div>
      <div>
        <label htmlFor="demo-contact" className="text-[0.9rem] font-medium">
          Email or phone
        </label>
        <input type="text" inputMode="email" autoComplete="email" required {...field("contact")} />
        {error("contact")}
      </div>
      <div>
        <label htmlFor="demo-lines" className="text-[0.9rem] font-medium">
          Number of sewing lines
        </label>
        <select required {...field("lines")}>
          <option value="">Choose…</option>
          {LINE_OPTIONS.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
        {error("lines")}
      </div>
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center">
        <button type="submit" className="fb-btn fb-btn-solid disabled:opacity-60" disabled={status.kind === "sending"}>
          {status.kind === "sending" ? "Sending…" : "Book a demo"}
        </button>
        <p id="demo-status" role="status" aria-live="polite" className={`text-[0.9rem] ${statusColor}`}>
          {status.message}
        </p>
      </div>
    </form>
  );
}

export function DemoSection() {
  return (
    <section id="demo" aria-labelledby="demo-title" className="relative z-20 border-t border-fb-line bg-fb-ink">
      <div className="mx-auto grid max-w-[1200px] gap-12 px-4 py-24 md:grid-cols-[1fr_1.1fr] md:gap-20 md:px-8 md:py-32">
        <div>
          <h2 id="demo-title" className="font-display text-[2.6rem] font-extrabold leading-none md:text-[3.6rem]">
            Book a demo
          </h2>
          <p className="mt-5 max-w-[30rem] leading-relaxed text-fb-text/90">
            Tell us a little about your factory. We&apos;ll walk you through FactoryBrain using examples from a plant
            like yours, and talk through what it would take to start on one floor.
          </p>
        </div>
        <DemoForm />
      </div>
    </section>
  );
}
