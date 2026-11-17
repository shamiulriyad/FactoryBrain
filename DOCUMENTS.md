# Factory documentation automation

One document engine for every department (production, HR, store, quality,
merchandising, compliance, commercial, devices). Reports that already exist as
records are **generated**, forms are **calculated and checked**, uploaded
files are **read** (rules first, external AI only for what is still missing),
and every document is **versioned, reviewed / approved according to its type,
downloadable as PDF / Excel / Word, and audited**. AI suggests; people decide.

- Catalog (types, fields, risk, default approval): `BunonBrain.Application/Documents/DocumentCatalog.cs`
- Calculations, AQL, validation, duplicate keys: `BunonBrain.Application/Documents/DocumentRules.cs`
- Engine: `BunonBrain.Infrastructure/Documents/*` (service, generators, enrichment / cross-checks,
  AI client + extractor, file store and readers, PDF / XLSX / DOCX writers, automation worker, inventory, notifications)
- API: `BunonBrain.Api/Controllers/DocumentsController.cs` (`/api/documents`, `/api/inventory`, `/api/notifications`)
- UI: `/app/documents` (My work, All documents, New document, document page, Status board, Settings & AI usage), `/app/inventory`, the bell in the top bar
- Migrations: `20261116172010_AddDocumentAutomation` (additive: 9 new tables), `20261116210109_AddRollsHolidaysAndSchedules`
  (additive: `inventory_rolls`, `inventory_roll_movements`, `factory_holidays`, `document_schedule_runs`; 3 schedule columns on `document_type_settings`)

## 1. Who does what

| Role | Documents |
|---|---|
| Factory Manager | Views all categories, reviews, approves (incl. high-risk), status board |
| Production Supervisor | Prepares production documents **for their own lines** (line-wise, cutting, sewing, finishing, WIP, downtime) |
| HR / Attendance Officer | Prepares and reviews attendance, late / absent, overtime, leave, employee information, payroll input |
| IT / Device Administrator | Prepares device log reports and maintenance records |
| Monitoring Operator | Status board only: missing, pending, overdue documents (titles and status, no content) |
| Worker | Nothing to manage (source of attendance and production data) |
| **Document Controller** (new) | Every category: tracks, verifies, archives, sets approval levels / schedules, status board. Approves nothing. |
| **Merchandiser** (new) | PO, order sheet, BOM, costing sheet, buyer instructions (+ merchandising module) |
| **Store / Inventory Officer** (new) | GRN, requisition, issue note, stock register, stock balance; store items |
| **QC / QA Officer** (new) | Inline / end-line / final inspection, defect, rework, AQL, CAPA (+ QC module) |
| **Compliance Officer** (new) | Compliance audit, safety inspection, training record |
| **Commercial / Shipping Officer** (new) | Commercial invoice, packing list, shipment documents |
| Factory Admin / Super Admin | Everything (as before) |

A person may hold several roles; permissions add up. Access =
permission (`documents.view | create | review | approve | approve_high | control | monitor`)
**and** the category permission (`documents.cat.<department>`) **and** the factory
(and, for supervisors, the line). Records outside the scope answer 404.

## 2. Document types (38)

Generated from records (G) or forms with calculations (F):

- **Production (G):** Daily Production Report (auto daily, expected daily), Line-wise, Cutting, Sewing, Finishing, WIP, Downtime (output records + REAL machine stoppages).
- **HR:** Attendance (G, auto daily, expected daily; absent = scheduled by shift assignment with no check-in, only once the day is over; **approved leave and holidays are never absence**), Late / Absent (G), Overtime (G), Payroll Input (G, high risk; present, leave and absent days; no salary calculated, unconfirmed OT flagged), Leave Record (F; days calculated, overlaps refused), Employee Information (F, filled from the worker directory, no personal contact data).
- **Store:** GRN (F; amounts / totals; accepted + rejected = received; **approval adds stock**, creating item codes; optional **fabric rolls** table), Material Requisition (F; names, units and balances filled), Material Issue Note (F; **cannot issue more than the balance**, checked against the requisition; **approval removes stock**; optional **rolls issued** table), Stock Register (G, by item category), Stock Balance (G, reorder flags, rolls in stock).
- **Quality:** Inline / End-line / Final Inspection (G, from QC inspections), Defect Report (G, DHU), Rework Report (G), AQL Report (F; ISO 2859-1 single sampling normal inspection, levels I–III, AQL 1.0–6.5, arrows resolved, any critical fails), CAPA (F).
- **Merchandising (F):** Purchase Order (high risk; fill from buyer order; breakdown vs quantity), Order Sheet, BOM (required qty = order × consumption × (1 + wastage)), Costing Sheet (high risk; FOB = (cost + commercial) × (1 + margin); quoted price below cost / margin flagged), Buyer Instruction Sheet.
- **Compliance (F):** Compliance Audit (high risk; finding counts; open major / critical findings need a corrective action), Safety Inspection, Training Record (attendees named from the directory; filed without approval by default).
- **Commercial (F):** Commercial Invoice (high risk; amounts; checked against the buyer order's quantity / price / currency and the packing list), Packing List (carton ranges must not overlap; pieces and weights totalled; checked against the invoice), Shipment Documents (high risk; invoice and packing list must exist; ETA ≥ ETD).
- **Devices:** Device Log Report (G), Maintenance Record (F; machine details filled).

Generated documents hold figures that people can't type over: fix the source
record and press **Refresh from records**; people add remarks. Missing source
data is never filled in — it is listed as a data gap (e.g. operators not
recorded, inspections not reviewed, punches missing).

## 3. Workflow

`DRAFT → SUBMITTED → UNDER_REVIEW → APPROVED | REJECTED`, `APPROVED → ARCHIVED`.

- **Approval level per type and factory** (Settings): `NONE` (filed on submission), `SINGLE` (one approver), `REVIEW_AND_APPROVE` (a reviewer verifies, a different person approves). High-risk types can never be `NONE`; approving them needs `documents.approve_high`.
- Submit is refused while required values are missing or errors remain; the checks are re-run at submit and at approval (stock, references and other records may have changed).
- Nobody approves (or reviews) what they prepared or submitted; the verifier doesn't approve.
- Rejection needs a reason; the preparer edits (a new version) and resubmits. Approved documents can be reopened with a reason (new version), except store documents that already moved stock.
- Every save with changed content is a new **append-only version** (data, where each value came from, the checks, SHA-256). Every step is an **append-only event** and an `audit_log` entry (`document.*`, `inventory.*`).
- **Duplicates:** one live document per factory and key — the number field (invoice no., challan no. + supplier, PO no., packing list no., B/L) or type + period + line. Uploading the same file twice to a document is refused.

## 4. Files, extraction and AI

Uploads (PDF, DOCX, XLSX, CSV, TXT, JPG / PNG / WEBP, ≤ `DOCUMENT_MAX_MB`, identified by their bytes)
go to the private file store `DOCUMENT_STORAGE_PATH` and are served only through the
authenticated API (`nosniff`, sandboxed CSP, `no-store`), never by public URL.

Reading a file: text is parsed locally (PdfPig, DOCX, XLSX, CSV); labelled values
(`Invoice No: …`) and tables whose header names the type's columns are read by rules.
Only if required values are still missing — and AI is allowed — are **those fields only**
sent to the language model configured in **Settings → AI** (OpenAI / Anthropic / Gemini;
key stays on the server), with the text cut to `DOCUMENT_AI_MAX_CHARS`. Images are sent
to the model for OCR. The answer must be a JSON object; values are coerced to the
field schema, unknown keys dropped, and **values that don't appear in the document text are
rejected**. Results are *suggestions*: a person applies them to the form and saves; the
version records them as AI-sourced.

Other AI uses: classify an upload (keywords first, AI only when unsure), summarise a
document and explain its findings (only the figures and the check messages are sent),
draft a free-text field (marks missing facts as `[needs input: …]`). AI never approves,
rejects or decides.

Calls retry only on time-outs, 429 and 5xx (`DOCUMENT_AI_MAX_ATTEMPTS`, back-off), each file
can be read `DOCUMENT_AI_MAX_EXTRACTIONS` times, and every call is logged in the
append-only `ai_api_usage` table (purpose, provider, model, outcome, attempts, tokens,
characters — never prompts, document text, responses or keys). Settings shows 30-day usage.

## 5. Automation and notifications

`DocumentAutomationWorker` (every 30 min) runs **only when `DOCUMENT_AUTOMATION_ENABLED=true`** (default
**false**; never in tests):

1. For each generated type set to generate automatically (Settings), creates the draft of its **latest complete
   period**: daily types yesterday; period types (overtime, payroll input, stock register, defect, rework, device log)
   the previous **week** (configurable first day, default Saturday) or **month**; for the **whole factory** or **each
   active line** (line types). Only when its source records exist and no live document for that period and scope
   exists; notifies the preparers (of that line). Every attempt is a row in `document_schedule_runs` (unique per
   factory, type, period and scope): `CREATED`, `EXISTS` (made by hand), `NO_DATA` (retried for 3 days after the period
   for late data entry) or `FAILED` (error code and message kept; retried up to 3 times, then document controllers are
   notified once and `document.schedule_failed` is audited). Settings → *Scheduled generation* shows the log.
2. Reports expected daily documents missing for the last working day to preparers and monitoring operators.
3. Reminds reviewers / approvers of documents waiting longer than the type's decision time.

It acts as `system:document-automation` and never submits or approves. Submissions,
verifications and decisions notify only the people who must act next (or the preparer).
Notifications are de-duplicated per user and key; the bell shows them (`/api/notifications`).

## 6. Store

`inventory_items` (per factory, unique code) and append-only `stock_movements`
(RECEIPT from approved GRN, ISSUE from approved issue note, once per document line,
under a per-factory lock). Balances are always the sum of movements.

**Fabric rolls.** A GRN may list its fabric roll by roll (item, roll no., lot / dye lot, colour, shade, width,
quantity in the item's unit, warehouse, rack / bin). The rolls of an item must add up to its accepted quantity; a roll
is identified by supplier + lot + roll number and can exist once per factory (checked on the form and again under the
stock lock; unique index `UX_inventory_rolls_identity`). Approval creates `inventory_rolls` and RECEIPT
`inventory_roll_movements` linked to the item movement. An issue note lists the rolls it issues: each must exist (lot
given when the number is ambiguous), belong to an item on the note and hold enough; an item stocked by roll can only be
issued without rolls from its *loose* stock (item balance − stock on rolls). Approval re-checks everything under the
lock and posts ISSUE roll movements, so roll quantities always add up to the item movements. A roll's remaining
quantity and status (in stock, partly issued, issued) are derived from its movements; rolls and roll movements are
append-only (triggers). `/app/inventory` lists rolls; the stock balance report counts rolls in stock.

## 6a. Leave and holidays

Leave is recorded as Leave Record documents; one counts once it is **approved** (or archived after approval).
`factory_holidays` (HR, `/app/attendance/holidays`, attendance.manage) holds public and factory holidays; holidays
that have started can't be removed, so reported attendance stays as reported. Attendance statistics, workforce
analytics, the Attendance and Late / Absent reports and Payroll Input all treat a scheduled worker-day on a holiday or
in approved leave as *not expected*: never absent and outside the attendance rate (holiday wins over leave; someone
who checks in anyway is present and flagged). Recorded attendance sessions are never changed. `/app/inventory`
shows balances, reorder flags and the ledger; items can also be created / edited by
the store officer (code and unit are fixed once stock moved).

## 7. Configuration

| Variable | Default |
|---|---|
| `DOCUMENT_STORAGE_PATH` | `<content root>/App_Data/factory-documents` |
| `DOCUMENT_MAX_MB` / `DOCUMENT_MAX_PAGES` | 15 (≤ 25) / 100 |
| `DOCUMENT_AI_MODEL` | the LLM model of Settings → AI |
| `DOCUMENT_AI_MAX_CHARS` / `DOCUMENT_AI_TIMEOUT_SECONDS` / `DOCUMENT_AI_MAX_ATTEMPTS` / `DOCUMENT_AI_MAX_EXTRACTIONS` | 12000 / 60 / 3 / 3 |
| `DOCUMENT_AUTOMATION_ENABLED` | **false** (must be set to `true` explicitly) |

## 8. PDF output

PDF exports (documents and analytics reports) use SkiaSharp's PDF backend with HarfBuzz shaping and embedded
Noto Sans + Noto Sans Bengali (SIL OFL, `BunonBrain.Infrastructure/Documents/Fonts`): Bangla conjuncts, vowel signs
and reordering render correctly, mixed Bangla / English lines and tables keep their layout, and text is selectable.

## 9. Known limitations

- QC inspections, merchandising orders and production orders predate factories: QC
  inspections without a line are left out of QC reports (counted as a data gap); buyer-order
  look-ups are not factory-partitioned.
- Leave is whole days (no half-day leave); the reserved `leave.manage` permission is still unused (leave goes through the document workflow).
- PDF fonts are embedded whole (Skia's build has no subsetter): a PDF is about 0.8 MB. Text extractors that ignore
  `/ActualText` (e.g. PdfPig) read complex Bangla clusters in visual order; viewers and MuPDF read them correctly.
- Fabric rolls from the fabric-inspection module (`fabric_rolls`) are not linked to store rolls; rolls can't be moved
  between warehouses after receipt (corrections are new documents).
- Uploaded files use the local private file store (as the other modules); no Supabase Storage bucket.
- Scheduled generation creates only the latest complete period (no back-fill of older periods).
