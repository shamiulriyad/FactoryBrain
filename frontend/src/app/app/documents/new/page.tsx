"use client";

import { useRouter } from "next/navigation";
import { useEffect, useMemo, useState } from "react";
import { FileUp, Sparkles, Wand2 } from "lucide-react";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { DOCS, DocRiskBadge, DocsPage, type DocScope } from "@/components/documents/shared";
import { errorMessage } from "@/lib/api";
import { APPROVAL_TEXT, docsApi, type DocType } from "@/lib/documents";

export default function Page() {
  return (
    <DocsPage title="New document" testId="documents-new"
      description="Generate a report from the factory's records, fill in a form (or start from the last approved one), fill it from a buyer order / worker / machine, or upload a file and let BunonBrain read it.">
      {(scope) => <NewDoc scope={scope} />}
    </DocsPage>
  );
}

type Mode = "generate" | "form" | "template" | "prefill" | "upload";

function yesterday(today: string) {
  const d = new Date(`${today}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() - 1);
  return d.toISOString().slice(0, 10);
}

function NewDoc({ scope }: { scope: DocScope }) {
  const { factoryId, catalog, q } = scope;
  const router = useRouter();
  const creatable = catalog.types.filter((t) => t.canCreate);
  const [typeCode, setTypeCode] = useState(q.get("type") || "");
  const type: DocType | undefined = creatable.find((t) => t.code === typeCode);
  const prefillField = type?.fields.find((f) => f.lookup && ["merch_order", "worker", "machine"].includes(f.lookup));
  const modes = useMemo<{ id: Mode; label: string; hint: string }[]>(() => {
    if (!type) return [];
    if (type.generated) return [{ id: "generate", label: "Generate from records", hint: "Figures are read from the recorded data; you add remarks and submit." }];
    const m: { id: Mode; label: string; hint: string }[] = [{ id: "form", label: "Blank form", hint: "Type the values; totals and checks are calculated for you." }];
    m.push({ id: "template", label: "From the last approved one", hint: "Copies what repeats (supplier, checklist, instructions); numbers and dates are left for you." });
    if (prefillField) m.push({ id: "prefill", label: `Fill from ${prefillField.lookup === "merch_order" ? "a buyer order" : prefillField.lookup === "worker" ? "a worker" : "a machine"}`, hint: `Give the ${prefillField.label}.` });
    m.push({ id: "upload", label: "Upload a file and read it", hint: "PDF, DOCX, XLSX, CSV or an image. Rules read labelled values first; AI is asked only for what is still missing." });
    return m;
  }, [type, prefillField]);
  const [mode, setMode] = useState<Mode>("form");
  useEffect(() => { if (modes.length && !modes.some((m) => m.id === mode)) setMode(modes[0].id); }, [modes, mode]);

  const [from, setFrom] = useState(yesterday(catalog.today));
  const [to, setTo] = useState(yesterday(catalog.today));
  const [line, setLine] = useState(catalog.can.lineScoped && catalog.lines.length ? catalog.lines[0].id : "");
  const [params, setParams] = useState<Record<string, string>>({});
  const [prefillKey, setPrefillKey] = useState("");
  const [file, setFile] = useState<File | null>(null);
  const [useAi, setUseAi] = useState(catalog.ai.configured);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [detect, setDetect] = useState<string | null>(null);

  async function classify(f: File) {
    setBusy("Detecting the document type…"); setDetect(null); setError(null);
    try {
      const r = await docsApi.classify(factoryId, f, useAi);
      if (r.typeCode && creatable.some((t) => t.code === r.typeCode)) {
        setTypeCode(r.typeCode); setMode("upload");
        setDetect(`Looks like ${creatable.find((t) => t.code === r.typeCode)!.name} (${r.method === "ai" ? "AI" : "keywords"}${r.confidence ? `, ${Math.round(r.confidence * 100)}%` : ""}). ${r.reason ?? ""} Check it before continuing.`);
      } else setDetect(r.reason ?? "The type could not be detected; choose it.");
    } catch (e) { setError(errorMessage(e)); } finally { setBusy(null); }
  }

  async function create() {
    if (!type) return;
    setError(null);
    try {
      setBusy(mode === "generate" ? "Reading the records…" : "Creating…");
      const created = await docsApi.create({
        factoryId, typeCode: type.code, mode, lineId: line || null,
        periodFrom: type.period === "NONE" ? null : from, periodTo: type.period === "RANGE" ? to : null,
        parameters: type.parameters.length ? params : null, prefillKey: mode === "prefill" ? prefillKey : null
      });
      const id = created.document.id;
      if (mode === "upload" && file) {
        setBusy("Uploading…");
        const up = await docsApi.upload(id, file);
        setBusy(useAi ? "Reading the file (rules, then AI for what is missing)…" : "Reading the file…");
        await docsApi.extract(id, up.id, useAi);
      }
      router.push(`${DOCS}/${id}?factory=${factoryId}`);
    } catch (e) {
      setError(errorMessage(e));
      setBusy(null);
    }
  }

  const needsFile = mode === "upload" && !file;
  const needsKey = mode === "prefill" && !prefillKey.trim();
  const needsLine = type?.code === "production.line" && !line;

  return (
    <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_320px]">
      <Panel title="What document?">
        <div className="space-y-4">
          <Field label="Document type">
            {(ids) => (
              <Select id={ids.id} value={typeCode} onChange={(e) => { setTypeCode(e.target.value); setDetect(null); }} data-testid="new-type">
                <option value="">Choose…</option>
                {catalog.categoryList.map((c) => (
                  <optgroup key={c.code} label={c.name}>
                    {creatable.filter((t) => t.category === c.code).map((t) => <option key={t.code} value={t.code}>{t.name}</option>)}
                  </optgroup>
                ))}
              </Select>
            )}
          </Field>
          <div className="bg-surface-2 rounded-md p-3 text-caption">
            <p className="font-medium mb-1 inline-flex items-center gap-1"><FileUp size={14} /> Not sure which type? Detect it from the file</p>
            <input type="file" accept=".pdf,.docx,.xlsx,.csv,.txt,.jpg,.jpeg,.png,.webp" onChange={(e) => { const f = e.target.files?.[0] ?? null; setFile(f); if (f) void classify(f); }} data-testid="new-detect-file" />
            {detect && <p className="mt-2">{detect}</p>}
          </div>

          {type && (
            <>
              <p className="text-caption text-fg-secondary">{type.description}</p>
              <div className="flex flex-wrap gap-2">
                <Badge tone="info">{APPROVAL_TEXT[type.approvalLevel]}</Badge>
                <DocRiskBadge risk={type.risk} />
                {type.generated && <Badge tone="accent">From records</Badge>}
              </div>
              <fieldset className="space-y-2">
                <legend className="field-label">How to start</legend>
                {modes.map((m) => (
                  <label key={m.id} className="flex gap-2 items-start text-body cursor-pointer">
                    <input type="radio" name="mode" checked={mode === m.id} onChange={() => setMode(m.id)} className="mt-1" data-testid={`mode-${m.id}`} />
                    <span>{m.label}<span className="block text-caption text-fg-tertiary">{m.hint}</span></span>
                  </label>
                ))}
              </fieldset>
              {type.period !== "NONE" && (
                <div className="flex flex-wrap gap-3">
                  <Field label={type.period === "DAY" ? "Date" : "From"}>{(ids) => <Input id={ids.id} type="date" max={catalog.today} value={from} onChange={(e) => setFrom(e.target.value)} data-testid="new-from" />}</Field>
                  {type.period === "RANGE" && <Field label="To">{(ids) => <Input id={ids.id} type="date" max={catalog.today} value={to} onChange={(e) => setTo(e.target.value)} />}</Field>}
                </div>
              )}
              {type.lineOption && catalog.lines.length > 0 && (
                <Field label="Production line" hint={catalog.can.lineScoped ? "Supervisors prepare documents for their own lines." : "Leave empty for the whole factory."}>
                  {(ids) => (
                    <Select id={ids.id} value={line} onChange={(e) => setLine(e.target.value)} data-testid="new-line">
                      {!catalog.can.lineScoped && <option value="">Whole factory</option>}
                      {catalog.lines.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
                    </Select>
                  )}
                </Field>
              )}
              {type.parameters.map((p) => (
                <Field key={p.key} label={p.label}>
                  {(ids) => (
                    <Select id={ids.id} value={params[p.key] ?? ""} onChange={(e) => setParams({ ...params, [p.key]: e.target.value })}>
                      {(p.options ?? []).map((o) => <option key={o} value={o}>{o}</option>)}
                    </Select>
                  )}
                </Field>
              ))}
              {mode === "prefill" && prefillField && (
                <Field label={prefillField.label}>{(ids) => <Input id={ids.id} value={prefillKey} onChange={(e) => setPrefillKey(e.target.value)} data-testid="new-prefill" />}</Field>
              )}
              {mode === "upload" && (
                <div className="space-y-2">
                  <Field label="File" hint={file ? `${file.name} · ${(file.size / 1024).toFixed(0)} KB` : "Stored privately; only people who can see this document can open it."}>
                    {(ids) => <input id={ids.id} type="file" accept=".pdf,.docx,.xlsx,.csv,.txt,.jpg,.jpeg,.png,.webp" onChange={(e) => setFile(e.target.files?.[0] ?? null)} data-testid="new-file" />}
                  </Field>
                  <label className="inline-flex items-center gap-2 text-caption">
                    <input type="checkbox" checked={useAi} disabled={!catalog.ai.configured} onChange={(e) => setUseAi(e.target.checked)} />
                    <Sparkles size={13} /> Let AI read values the rules cannot find (images always need it)
                  </label>
                  {!catalog.ai.configured && <p className="text-caption text-fg-tertiary">{catalog.ai.reason}</p>}
                </div>
              )}
            </>
          )}
          {error && <Callout tone="danger" title="Could not create the document">{error}</Callout>}
          <div className="flex gap-2">
            <Button onClick={() => void create()} disabled={!type || !!busy || needsFile || needsKey || needsLine} iconLeft={<Wand2 size={14} />} data-testid="new-create">
              {busy ?? (mode === "generate" ? "Generate" : "Create")}
            </Button>
          </div>
        </div>
      </Panel>
      <Panel title="How documents flow">
        <ol className="list-decimal pl-5 space-y-1.5 text-caption text-fg-secondary">
          <li>Draft: values come from records, a file, or you. Missing values are marked — nothing is invented.</li>
          <li>Submit: blocked until required values are present and rules pass.</li>
          <li>Review and approval follow the document type setting; you never approve your own document.</li>
          <li>Rejected documents come back with a reason; correct and resubmit. Every change is a new version.</li>
          <li>Download as PDF, Excel or Word at any time. Approved GRNs and issue notes move stock.</li>
        </ol>
      </Panel>
    </div>
  );
}
