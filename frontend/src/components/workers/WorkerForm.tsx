"use client";

// The worker form, shared by Add worker and Edit worker. Validates on the
// client with the same rules as the API (lib/workers.ts) and shows the API's
// own field errors when it still refuses. The Worker ID is never entered
// here: the server generates it.

import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import Link from "next/link";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Field, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Callout } from "@/components/ui/Callout";
import { SkeletonRows } from "@/components/ui/Status";
import { errorMessage, isApiError } from "@/lib/api";
import { useCan } from "@/lib/auth";
import { P } from "@/lib/access";
import { EMPLOYMENT_TYPES, GENDERS, validateWorkerInput, type WorkerInput } from "@/lib/workers";
import { useFactoryOrg } from "./shared";

type Props = {
  factoryId: string;
  initial: WorkerInput;
  mode: "create" | "edit";
  submitLabel: string;
  onSubmit: (input: WorkerInput) => Promise<void>;
  onCancel: () => void;
  /** Rendered above the sections (e.g. the factory choice on Add worker). */
  header?: ReactNode;
};

/** API field names → form field names, for server-side validation errors. */
const SERVER_FIELD: Record<string, keyof WorkerInput> = {
  fullName: "fullName", phone: "phone", joinedOn: "joinedOn", departmentId: "departmentId", designation: "designation",
  employmentType: "employmentType", employeeCode: "employeeCode", gender: "gender", dateOfBirth: "dateOfBirth", email: "email",
  presentAddress: "presentAddress", emergencyContactName: "emergencyContactName", emergencyContactPhone: "emergencyContactPhone",
  emergencyContactRelation: "emergencyContactRelation", floorId: "floorId", lineId: "lineId", workstation: "workstation", grade: "grade"
};

/** API error codes that belong to one field. */
const CODE_FIELD: Record<string, keyof WorkerInput> = {
  employee_code_taken: "employeeCode",
  department_required: "departmentId",
  department_not_in_factory: "departmentId",
  department_inactive: "departmentId",
  floor_not_in_factory: "floorId",
  floor_inactive: "floorId",
  line_not_in_factory: "lineId",
  line_out_of_scope: "lineId"
};

export function WorkerForm({ factoryId, initial, mode, submitLabel, onSubmit, onCancel, header }: Props) {
  const [v, setV] = useState<WorkerInput>(initial);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [touched, setTouched] = useState(false);
  const { departments, floors, lines } = useFactoryOrg(factoryId);
  const canManageDepartments = useCan(P.DepartmentsManage);

  useEffect(() => setV(initial), [initial]);

  // A department / floor / line of another factory is never kept when the factory changes.
  useEffect(() => {
    if (mode === "create") setV((cur) => ({ ...cur, departmentId: "", floorId: "", lineId: "", workstation: "" }));
  }, [factoryId, mode]);

  function set<K extends keyof WorkerInput>(key: K, value: WorkerInput[K]) {
    setV((cur) => {
      const next = { ...cur, [key]: value };
      if (key === "lineId" && !value) next.workstation = "";
      if (touched) setErrors(validateWorkerInput(next));
      return next;
    });
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    setTouched(true);
    setFormError(null);
    const found = validateWorkerInput(v);
    setErrors(found);
    if (Object.keys(found).length > 0) {
      setFormError("Some fields need attention.");
      document.querySelector<HTMLElement>("[aria-invalid='true']")?.focus();
      return;
    }
    setBusy(true);
    try {
      await onSubmit(v);
    } catch (err) {
      if (isApiError(err)) {
        const fieldErrors: Record<string, string> = {};
        for (const [k, msgs] of Object.entries(err.fieldErrors)) {
          const field = SERVER_FIELD[k];
          if (field && msgs[0]) fieldErrors[field] = msgs[0];
        }
        const code = (err.body as { code?: string } | null)?.code;
        if (code && CODE_FIELD[code]) fieldErrors[CODE_FIELD[code]] = err.message;
        setErrors(fieldErrors);
        setFormError(Object.keys(fieldErrors).length > 0 ? "The server did not accept some values." : err.message);
      } else setFormError(errorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  const activeDepartments = (departments.data ?? []).filter((d) => d.isActive || d.id === v.departmentId);
  const activeFloors = (floors.data ?? []).filter((d) => d.isActive || d.id === v.floorId);
  const noDepartments = departments.data !== null && activeDepartments.length === 0;

  const text = (key: keyof WorkerInput, label: string, opts: { hint?: string; type?: string; max?: number; required?: boolean; placeholder?: string; mono?: boolean } = {}) => (
    <Field label={opts.required ? <>{label} <span className="text-[var(--risk-high)]" aria-hidden>*</span></> : label} hint={opts.hint} error={errors[key]}>
      {({ id, describedBy, invalid }) => (
        <Input
          id={id}
          type={opts.type ?? "text"}
          value={v[key]}
          maxLength={opts.max}
          placeholder={opts.placeholder}
          required={opts.required}
          aria-required={opts.required}
          aria-invalid={invalid}
          aria-describedby={describedBy}
          className={opts.mono ? "font-mono" : undefined}
          onChange={(e) => set(key, e.target.value)}
          data-testid={`worker-${key}`}
        />
      )}
    </Field>
  );

  return (
    <form onSubmit={(e) => void submit(e)} noValidate className="space-y-4" data-testid="worker-form">
      {header}

      <Panel title="Personal information" subtitle="Name and contact details. Personal details are visible only to HR, administrators and the worker.">
        <div className="grid gap-4 sm:grid-cols-2">
          {text("fullName", "Full name", { required: true, max: 120 })}
          {text("phone", "Phone number", { required: true, max: 32, type: "tel", placeholder: "+8801XXXXXXXXX" })}
          {text("email", "Email", { type: "email", max: 256, hint: "Optional. Not used to sign in." })}
          <Field label="Gender" hint="Optional." error={errors.gender}>
            {({ id, describedBy }) => (
              <Select id={id} aria-describedby={describedBy} value={v.gender} onChange={(e) => set("gender", e.target.value)} data-testid="worker-gender">
                <option value="">Prefer not to record</option>
                {GENDERS.map((g) => <option key={g} value={g}>{g}</option>)}
              </Select>
            )}
          </Field>
          {text("dateOfBirth", "Date of birth", { type: "date", hint: "Optional." })}
          <Field label="Present address" hint="Optional." error={errors.presentAddress} className="sm:col-span-2">
            {({ id, describedBy, invalid }) => (
              <TextArea id={id} rows={2} maxLength={500} aria-invalid={invalid} aria-describedby={describedBy} value={v.presentAddress}
                onChange={(e) => set("presentAddress", e.target.value)} />
            )}
          </Field>
        </div>
      </Panel>

      <Panel title="Employment" subtitle="The Worker ID is generated when the worker is registered and never changes.">
        {departments.loading ? (
          <SkeletonRows rows={2} rowHeight="h-10" />
        ) : (
          <div className="grid gap-4 sm:grid-cols-2">
            {text("joinedOn", "Joining date", { type: "date", required: true })}
            <Field label={<>Department <span className="text-[var(--risk-high)]" aria-hidden>*</span></>} error={errors.departmentId ?? (departments.error ? errorMessage(departments.error) : null)}>
              {({ id, describedBy, invalid }) => (
                <Select id={id} aria-required aria-invalid={invalid} aria-describedby={describedBy} value={v.departmentId}
                  onChange={(e) => set("departmentId", e.target.value)} disabled={noDepartments} data-testid="worker-departmentId">
                  <option value="">{noDepartments ? "No departments yet" : "Choose a department…"}</option>
                  {activeDepartments.map((d) => <option key={d.id} value={d.id}>{d.name}{d.isActive ? "" : " (deactivated)"}</option>)}
                </Select>
              )}
            </Field>
            {text("designation", "Designation / job title", { required: true, max: 80, placeholder: "e.g. Sewing Operator" })}
            <Field label="Employment type" error={errors.employmentType}>
              {({ id }) => (
                <Select id={id} value={v.employmentType} onChange={(e) => set("employmentType", e.target.value)} data-testid="worker-employmentType">
                  {EMPLOYMENT_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
                </Select>
              )}
            </Field>
            {text("employeeCode", "Employee / staff number", { max: 32, mono: true, hint: "Optional. The factory's own number, if it differs from the Worker ID." })}
            {text("grade", "Grade", { max: 32, hint: "Optional." })}
            {noDepartments && (
              <Callout tone="warning" className="sm:col-span-2">
                This factory has no departments yet. {canManageDepartments
                  ? <>Create them under <Link href="/app/admin/factories" className="underline">Factories &amp; lines</Link> first.</>
                  : "Ask your Factory Admin to create them."}
              </Callout>
            )}
          </div>
        )}
      </Panel>

      <Panel title="Work placement" subtitle="Optional. Workstation assignment and shifts come with later modules.">
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Production floor" error={errors.floorId}>
            {({ id }) => (
              <Select id={id} value={v.floorId} onChange={(e) => set("floorId", e.target.value)} data-testid="worker-floorId">
                <option value="">Not assigned</option>
                {activeFloors.map((f) => <option key={f.id} value={f.id}>{f.name}{f.isActive ? "" : " (deactivated)"}</option>)}
              </Select>
            )}
          </Field>
          <Field label="Production line" error={errors.lineId ?? (lines.error ? errorMessage(lines.error) : null)}>
            {({ id }) => (
              <Select id={id} value={v.lineId} onChange={(e) => set("lineId", e.target.value)} data-testid="worker-lineId">
                <option value="">Not assigned</option>
                {(lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
              </Select>
            )}
          </Field>
          {mode === "create" && v.lineId && text("workstation", "Workstation label", { max: 32, hint: "Optional, e.g. WS-04." })}
        </div>
      </Panel>

      <Panel title="Emergency contact" subtitle="Optional.">
        <div className="grid gap-4 sm:grid-cols-3">
          {text("emergencyContactName", "Name", { max: 120 })}
          {text("emergencyContactPhone", "Phone", { max: 32, type: "tel" })}
          {text("emergencyContactRelation", "Relation", { max: 40, placeholder: "e.g. Mother" })}
        </div>
      </Panel>

      {formError && <Callout tone="danger" role="alert" testId="worker-form-error">{formError}</Callout>}

      <div className="flex flex-wrap justify-end gap-2 pb-2">
        <Button type="button" variant="ghost" onClick={onCancel} disabled={busy}>Cancel</Button>
        <Button type="submit" variant="primary" disabled={busy || noDepartments} data-testid="worker-submit">
          {busy ? "Saving…" : submitLabel}
        </Button>
      </div>
    </form>
  );
}
