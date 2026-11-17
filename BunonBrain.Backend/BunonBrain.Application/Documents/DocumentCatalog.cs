using BunonBrain.Application.Access;

namespace BunonBrain.Application.Documents;

public static class DocumentStatuses
{
    public const string Draft = "DRAFT";
    public const string Submitted = "SUBMITTED";
    public const string UnderReview = "UNDER_REVIEW";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Archived = "ARCHIVED";
    public static readonly string[] All = { Draft, Submitted, UnderReview, Approved, Rejected, Archived };
    /// <summary>Waiting for a reviewer or approver.</summary>
    public static readonly string[] Pending = { Submitted, UnderReview };
    /// <summary>Content can be edited (by a preparer).</summary>
    public static bool Editable(string s) => s is Draft or Rejected;
}

public static class ApprovalLevels
{
    /// <summary>Filed on submission; no approver (low-risk records and system reports).</summary>
    public const string None = "NONE";
    /// <summary>One approver (not the preparer).</summary>
    public const string Single = "SINGLE";
    /// <summary>Verified by a reviewer, then approved by a different person.</summary>
    public const string ReviewAndApprove = "REVIEW_AND_APPROVE";
    public static readonly string[] All = { None, Single, ReviewAndApprove };
}

public static class DocumentRisks
{
    public const string Low = "LOW";
    public const string Normal = "NORMAL";
    /// <summary>Financial, compliance and shipment documents: approval needs <c>documents.approve_high</c>.</summary>
    public const string High = "HIGH";
}

public static class DocumentSources
{
    public const string Form = "FORM";
    public const string Generated = "GENERATED";
    public const string Upload = "UPLOAD";
    public const string Template = "TEMPLATE";
}

/// <summary>Document categories (departments) and the permission that opens each.</summary>
public static class DocumentCategories
{
    public const string Production = "production";
    public const string Hr = "hr";
    public const string Store = "store";
    public const string Quality = "quality";
    public const string Merchandising = "merchandising";
    public const string Compliance = "compliance";
    public const string Commercial = "commercial";
    public const string Devices = "devices";

    public static readonly IReadOnlyList<(string Code, string Name, string Permission, string Prefix)> All = new[]
    {
        (Production, "Production", Permissions.DocCatProduction, "PRD"),
        (Hr, "HR & attendance", Permissions.DocCatHr, "HR"),
        (Store, "Store & inventory", Permissions.DocCatStore, "STR"),
        (Quality, "Quality", Permissions.DocCatQuality, "QC"),
        (Merchandising, "Merchandising", Permissions.DocCatMerchandising, "MER"),
        (Compliance, "Compliance", Permissions.DocCatCompliance, "CMP"),
        (Commercial, "Commercial & shipping", Permissions.DocCatCommercial, "COM"),
        (Devices, "Devices & maintenance", Permissions.DocCatDevices, "DEV"),
    };

    public static string? PermissionFor(string category) => All.FirstOrDefault(c => c.Code == category).Permission;

    public static string PrefixFor(string category) => All.FirstOrDefault(c => c.Code == category).Prefix ?? "DOC";

    /// <summary>The categories a caller can work in: every one with <c>documents.control</c>, else those whose permission they hold.</summary>
    public static IReadOnlyList<string> For(AccessContext c) =>
        All.Where(x => c.Has(Permissions.DocumentsControl) || c.Has(x.Permission)).Select(x => x.Code).ToArray();
}

public enum FieldKind { Text, TextArea, Number, Integer, Date, Select, Boolean, Table }

/// <summary>One field of a document type (or one column of a table field).</summary>
/// <param name="Computed">Calculated by the server (totals, AQL, balances) or filled from records; never typed by a person.</param>
/// <param name="Lookup">Auto-fill from records: <c>item</c> (store item code), <c>worker</c> (Worker ID), <c>merch_order</c> (buyer PO), <c>machine</c> (asset tag), <c>document</c> (a document reference).</param>
public sealed record FieldDef(
    string Key, string Label, FieldKind Kind, bool Required = false, IReadOnlyList<string>? Options = null,
    IReadOnlyList<FieldDef>? Columns = null, bool Computed = false, string? Help = null, string? Unit = null, string? Lookup = null);

public enum PeriodKind { None, Day, Range }

/// <summary>A document type: what it holds, how it is made and how it is approved by default.</summary>
public sealed record DocumentTypeDef(
    string Code,
    string Name,
    string Category,
    string Description,
    string Risk,
    string DefaultApproval,
    PeriodKind Period,
    /// <summary>Generated from operational records (deterministic); people add remarks, they don't retype figures.</summary>
    bool Generated,
    IReadOnlyList<FieldDef> Fields,
    /// <summary>The document can be about one production line (line-scoped access applies).</summary>
    bool LineOption = false,
    bool DefaultAutoGenerate = false,
    bool DefaultExpectedDaily = false,
    /// <summary>Field whose value identifies the document (invoice no., challan no. …) for duplicate prevention; otherwise the period is used.</summary>
    string? NumberField = null,
    /// <summary>Generation parameters (e.g. item category of a stock register).</summary>
    IReadOnlyList<FieldDef>? Parameters = null,
    /// <summary>Words that point at this type (deterministic classification of uploads).</summary>
    IReadOnlyList<string>? Keywords = null);

/// <summary>
/// Every document type BunonBrain automates, grouped by department. This is
/// the single definition the API, validation, generation, rendering, AI
/// extraction (field schemas) and the frontend's generic forms use; a new
/// type is a new entry here (plus a generator or calculator when it needs one).
/// </summary>
public static class DocumentCatalog
{
    private static FieldDef T(string k, string l, bool req = false, string? help = null, string? lookup = null) => new(k, l, FieldKind.Text, req, Help: help, Lookup: lookup);
    private static FieldDef A(string k, string l, bool req = false, string? help = null) => new(k, l, FieldKind.TextArea, req, Help: help);
    private static FieldDef N(string k, string l, bool req = false, string? unit = null) => new(k, l, FieldKind.Number, req, Unit: unit);
    private static FieldDef I(string k, string l, bool req = false, string? unit = null) => new(k, l, FieldKind.Integer, req, Unit: unit);
    private static FieldDef D(string k, string l, bool req = false) => new(k, l, FieldKind.Date, req);
    private static FieldDef S(string k, string l, bool req, params string[] options) => new(k, l, FieldKind.Select, req, options);
    private static FieldDef B(string k, string l) => new(k, l, FieldKind.Boolean);
    private static FieldDef C(string k, string l, FieldKind kind = FieldKind.Number, string? unit = null) => new(k, l, kind, Computed: true, Unit: unit);
    private static FieldDef CT(string k, string l) => new(k, l, FieldKind.Text, Computed: true);
    private static FieldDef Tbl(string k, string l, bool req, params FieldDef[] cols) => new(k, l, FieldKind.Table, req, Columns: cols);
    /// <summary>A table filled from records (read-only).</summary>
    private static FieldDef GTbl(string k, string l, params FieldDef[] cols) => new(k, l, FieldKind.Table, Columns: cols, Computed: true);

    private static readonly FieldDef Remarks = A("remarks", "Remarks");

    private static readonly string[] StageColumns = { "cutting", "sewingInput", "sewingOutput", "finishing", "packing" };

    public static readonly IReadOnlyList<DocumentTypeDef> All = new DocumentTypeDef[]
    {
        // ── Production ──────────────────────────────────────────────────────
        new("production.daily", "Daily Production Report", DocumentCategories.Production,
            "Factory output of one day by line and stage, with rework, rejects and downtime, from the recorded production output.",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.Day, Generated: true, DefaultAutoGenerate: true, DefaultExpectedDaily: true,
            Keywords: new[] { "daily production", "production report", "output" },
            Fields: new[]
            {
                C("cutting", "Cut (pcs)", FieldKind.Integer), C("sewingInput", "Sewing input (pcs)", FieldKind.Integer), C("sewingOutput", "Sewing output (pcs)", FieldKind.Integer),
                C("finishing", "Finished (pcs)", FieldKind.Integer), C("packing", "Packed (pcs)", FieldKind.Integer),
                C("rework", "Rework (pcs)", FieldKind.Integer), C("reject", "Rejected (pcs)", FieldKind.Integer), C("downtimeMinutes", "Downtime (min)", unit: "min"),
                GTbl("lines", "Output by line", CT("line", "Line"), C("cutting", "Cut", FieldKind.Integer), C("sewingInput", "Sew in", FieldKind.Integer),
                    C("sewingOutput", "Sew out", FieldKind.Integer), C("finishing", "Finished", FieldKind.Integer), C("packing", "Packed", FieldKind.Integer),
                    C("rework", "Rework", FieldKind.Integer), C("reject", "Reject", FieldKind.Integer), C("operators", "Operators (avg)"), C("downtimeMinutes", "Downtime min")),
                Remarks,
            }),
        new("production.line", "Line-wise Production Report", DocumentCategories.Production,
            "One line's output of one day by order and stage.",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.Day, Generated: true, LineOption: true,
            Keywords: new[] { "line wise", "line-wise", "line report", "hourly" },
            Fields: new[]
            {
                C("total", "Good pieces", FieldKind.Integer), C("rework", "Rework", FieldKind.Integer), C("reject", "Reject", FieldKind.Integer), C("downtimeMinutes", "Downtime (min)"),
                GTbl("rows", "Output by order and stage", CT("order", "Order"), CT("buyer", "Buyer"), CT("stage", "Stage"), C("quantity", "Good", FieldKind.Integer),
                    C("rework", "Rework", FieldKind.Integer), C("reject", "Reject", FieldKind.Integer), C("operators", "Operators (avg)")),
                Remarks,
            }),
        Stage("production.cutting", "Cutting Report", "Pieces cut per line and order.", new[] { "cutting" }, new[] { "cutting", "cut report", "lay" }),
        Stage("production.sewing", "Sewing Report", "Sewing input and output per line and order.", new[] { "sewingInput", "sewingOutput" }, new[] { "sewing", "sewing output", "sewing input" }),
        Stage("production.finishing", "Finishing Report", "Finished and packed pieces per line and order.", new[] { "finishing", "packing" }, new[] { "finishing", "packing report", "iron" }),
        new("production.wip", "WIP Report", DocumentCategories.Production,
            "Work in progress per open order up to a date: cut not yet in sewing, in sewing, sewn but not finished.",
            DocumentRisks.Low, ApprovalLevels.None, PeriodKind.Day, Generated: true, LineOption: true,
            Keywords: new[] { "wip", "work in progress" },
            Fields: new[]
            {
                C("cuttingWip", "Cutting WIP (pcs)", FieldKind.Integer), C("sewingWip", "Sewing WIP (pcs)", FieldKind.Integer), C("finishingWip", "Finishing WIP (pcs)", FieldKind.Integer),
                GTbl("orders", "WIP by order", CT("order", "Order"), CT("buyer", "Buyer"), C("orderQty", "Order qty", FieldKind.Integer), C("cut", "Cut", FieldKind.Integer),
                    C("sewingInput", "Sew in", FieldKind.Integer), C("sewingOutput", "Sew out", FieldKind.Integer), C("finishing", "Finished", FieldKind.Integer),
                    C("cuttingWip", "Cut WIP", FieldKind.Integer), C("sewingWip", "Sew WIP", FieldKind.Integer), C("finishingWip", "Finish WIP", FieldKind.Integer)),
                Remarks,
            }),
        new("production.downtime", "Downtime Report", DocumentCategories.Production,
            "Line downtime recorded with output and machine stoppages (stopped, error, maintenance, offline) of one day.",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.Day, Generated: true, LineOption: true,
            Keywords: new[] { "downtime", "breakdown", "stoppage" },
            Fields: new[]
            {
                C("lineDowntimeMinutes", "Recorded line downtime (min)"), C("machineDowntimeMinutes", "Machine stoppage (min)"),
                GTbl("lineDowntime", "Line downtime", CT("line", "Line"), CT("order", "Order"), CT("from", "From"), CT("to", "To"), C("minutes", "Minutes"), CT("reason", "Reason")),
                GTbl("machineDowntime", "Machine stoppages", CT("machine", "Machine"), CT("line", "Line"), CT("state", "State"), CT("from", "From"), CT("to", "To"), C("minutes", "Minutes"), CT("reason", "Reason")),
                Remarks,
            }),

        // ── HR ──────────────────────────────────────────────────────────────
        new("hr.attendance", "Attendance Report", DocumentCategories.Hr,
            "Attendance of one day from the attendance sessions (RFID, manual punches): present, late, early departure, missing punches, approved leave and absent scheduled workers. Holidays have no absence.",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.Day, Generated: true, DefaultAutoGenerate: true, DefaultExpectedDaily: true, LineOption: true,
            Keywords: new[] { "attendance", "present", "in time", "out time" },
            Fields: new[]
            {
                C("scheduled", "Scheduled", FieldKind.Integer), C("present", "Present", FieldKind.Integer), C("absent", "Absent", FieldKind.Integer),
                C("onLeave", "On approved leave", FieldKind.Integer), CT("holiday", "Holiday"), C("late", "Late", FieldKind.Integer),
                C("earlyDeparture", "Early departure", FieldKind.Integer), C("missingPunch", "Missing punch", FieldKind.Integer), C("netHours", "Net working hours", unit: "h"),
                GTbl("rows", "Workers", CT("workerCode", "Worker ID"), CT("name", "Name"), CT("line", "Line"), CT("checkIn", "In"), CT("checkOut", "Out"),
                    C("netMinutes", "Net min", FieldKind.Integer), C("lateMinutes", "Late min", FieldKind.Integer), CT("status", "Status")),
                Remarks,
            }),
        new("hr.late_absent", "Late / Absent Report", DocumentCategories.Hr,
            "Workers who were late, left early, missed a punch or were absent although scheduled, on one day (approved leave and holidays are not absence).",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.Day, Generated: true, LineOption: true,
            Keywords: new[] { "late", "absent", "absence" },
            Fields: new[]
            {
                C("late", "Late", FieldKind.Integer), C("absent", "Absent", FieldKind.Integer), C("onLeave", "On approved leave", FieldKind.Integer),
                C("earlyDeparture", "Early departure", FieldKind.Integer), C("missingPunch", "Missing punch", FieldKind.Integer),
                GTbl("rows", "Exceptions", CT("workerCode", "Worker ID"), CT("name", "Name"), CT("line", "Line"), CT("kind", "Kind"), C("minutes", "Minutes", FieldKind.Integer)),
                Remarks,
            }),
        new("hr.overtime", "Overtime Report", DocumentCategories.Hr,
            "Overtime requested, approved, worked (from attendance) and confirmed payable in a period.",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.Range, Generated: true, LineOption: true,
            Keywords: new[] { "overtime", "ot sheet", "extra hours" },
            Fields: new[]
            {
                C("requestedMinutes", "Requested (min)", FieldKind.Integer), C("approvedMinutes", "Approved (min)", FieldKind.Integer),
                C("actualMinutes", "Worked (min)", FieldKind.Integer), C("payableMinutes", "Payable, confirmed (min)", FieldKind.Integer), C("unconfirmed", "Not yet confirmed", FieldKind.Integer),
                GTbl("rows", "Overtime", CT("workerCode", "Worker ID"), CT("name", "Name"), CT("date", "Date"), C("requested", "Requested", FieldKind.Integer),
                    C("approved", "Approved", FieldKind.Integer), C("actual", "Worked", FieldKind.Integer), C("payable", "Payable", FieldKind.Integer), CT("status", "Status")),
                Remarks,
            }),
        new("hr.leave", "Leave Record", DocumentCategories.Hr,
            "A worker's leave: type, dates and reason. Days are counted by the server.",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.None, Generated: false,
            Keywords: new[] { "leave", "sick leave", "casual leave", "annual leave", "maternity" },
            Fields: new[]
            {
                T("workerCode", "Worker ID", true, lookup: "worker"), CT("workerName", "Worker name"), CT("line", "Line"),
                S("leaveType", "Leave type", true, "CASUAL", "SICK", "ANNUAL", "MATERNITY", "UNPAID", "OTHER"),
                D("fromDate", "From", true), D("toDate", "To", true), C("days", "Days", FieldKind.Integer), A("reason", "Reason", true), Remarks,
            }),
        new("hr.employee", "Employee Information", DocumentCategories.Hr,
            "Employment record of one worker, filled from the worker directory (no personal contact details).",
            DocumentRisks.Low, ApprovalLevels.Single, PeriodKind.None, Generated: false, NumberField: "workerCode",
            Keywords: new[] { "employee information", "personal file", "employee record", "joining" },
            Fields: new[]
            {
                T("workerCode", "Worker ID", true, lookup: "worker"), CT("fullName", "Full name"), CT("employeeCode", "Employee no."), CT("designation", "Designation"),
                CT("grade", "Grade"), CT("department", "Department"), CT("line", "Line"), CT("joinedOn", "Joined on"), CT("employmentType", "Employment type"), CT("status", "Status"),
                Remarks,
            }),
        new("hr.payroll_input", "Payroll Input Report", DocumentCategories.Hr,
            "Per-worker days present, on approved leave and absent, net hours, late count and confirmed payable overtime for a pay period. Input for payroll; no salary is calculated.",
            DocumentRisks.High, ApprovalLevels.ReviewAndApprove, PeriodKind.Range, Generated: true,
            Keywords: new[] { "payroll", "salary input", "wage" },
            Fields: new[]
            {
                C("workers", "Workers", FieldKind.Integer), C("presentDays", "Present days (total)", FieldKind.Integer), C("leaveDays", "Approved leave days (total)", FieldKind.Integer),
                C("absentDays", "Absent days (total)", FieldKind.Integer), C("holidays", "Holidays in the period", FieldKind.Integer), C("netHours", "Net hours (total)", unit: "h"),
                C("payableOtMinutes", "Payable OT (min)", FieldKind.Integer), C("unconfirmedOt", "OT not yet confirmed", FieldKind.Integer),
                GTbl("rows", "Workers", CT("workerCode", "Worker ID"), CT("name", "Name"), C("presentDays", "Present days", FieldKind.Integer),
                    C("leaveDays", "Leave days", FieldKind.Integer), C("absentDays", "Absent days", FieldKind.Integer), C("netHours", "Net hours"),
                    C("lateCount", "Late", FieldKind.Integer), C("payableOtMinutes", "Payable OT min", FieldKind.Integer), C("unconfirmedOt", "OT unconfirmed", FieldKind.Integer)),
                Remarks,
            }),

        // ── Store ───────────────────────────────────────────────────────────
        new("store.grn", "Goods Receive Note (GRN)", DocumentCategories.Store,
            "Materials received from a supplier. When approved, accepted quantities are added to stock (new item codes are created); fabric listed roll by roll becomes roll stock.",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.None, Generated: false, NumberField: "challanNo",
            Keywords: new[] { "goods receive", "goods received", "grn", "delivery challan", "challan" },
            Fields: new[]
            {
                T("supplier", "Supplier", true), T("challanNo", "Challan / delivery no.", true), T("poReference", "PO reference"), D("receivedOn", "Received on", true),
                Tbl("items", "Items", true, T("itemCode", "Item code", true, lookup: "item"), T("itemName", "Item name", true),
                    S("category", "Category", false, "FABRIC", "ACCESSORIES", "TRIMS", "PACKING", "CHEMICALS", "OTHER"), T("unit", "Unit", true),
                    N("orderedQty", "Ordered"), N("receivedQty", "Received", true), N("acceptedQty", "Accepted", true), N("rejectedQty", "Rejected"),
                    N("unitCost", "Unit cost"), C("amount", "Amount")),
                Tbl("rolls", "Fabric rolls (roll-wise receipt)", false, T("itemCode", "Item code", true, lookup: "item"), T("rollNumber", "Roll no.", true),
                    T("lotNumber", "Lot / dye lot"), T("color", "Colour"), T("shadeGroup", "Shade"), N("widthCm", "Width (cm)"), N("quantity", "Quantity", true),
                    T("warehouse", "Warehouse"), T("location", "Rack / bin")),
                C("totalAccepted", "Total accepted"), C("totalAmount", "Total amount"), C("rollCount", "Rolls", FieldKind.Integer), Remarks,
            }),
        new("store.requisition", "Material Requisition", DocumentCategories.Store,
            "A department's or line's request for materials from the store.",
            DocumentRisks.Low, ApprovalLevels.Single, PeriodKind.None, Generated: false,
            Keywords: new[] { "requisition", "material request", "indent" },
            Fields: new[]
            {
                T("department", "Requesting department / line", true), T("orderReference", "Order / style"), D("requiredBy", "Required by", true),
                Tbl("items", "Items", true, T("itemCode", "Item code", true, lookup: "item"), CT("itemName", "Item name"), CT("unit", "Unit"),
                    N("quantity", "Quantity", true), C("balance", "Store balance"), T("purpose", "Purpose")),
                Remarks,
            }),
        new("store.issue", "Material Issue Note", DocumentCategories.Store,
            "Materials issued from the store. When approved, quantities leave stock (and the rolls listed); issuing more than the item or roll balance is refused.",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.None, Generated: false,
            Keywords: new[] { "material issue", "issue note", "issue slip", "store issue" },
            Fields: new[]
            {
                T("requisitionReference", "Requisition (document reference)", lookup: "document"), T("issuedTo", "Issued to (department / line)", true), D("issuedOn", "Issued on", true),
                Tbl("items", "Items", true, T("itemCode", "Item code", true, lookup: "item"), CT("itemName", "Item name"), CT("unit", "Unit"),
                    N("quantity", "Quantity", true), C("balance", "Balance before issue")),
                Tbl("rolls", "Rolls issued", false, T("rollNumber", "Roll no.", true), T("lotNumber", "Lot / dye lot"), CT("itemCode", "Item"), CT("color", "Colour"),
                    N("quantity", "Quantity", true), C("remaining", "On the roll before issue")),
                Remarks,
            }),
        new("store.stock_register", "Fabric / Accessories Stock Register", DocumentCategories.Store,
            "Every receipt and issue of a period with running balances, from the stock ledger.",
            DocumentRisks.Low, ApprovalLevels.None, PeriodKind.Range, Generated: true,
            Parameters: new[] { S("itemCategory", "Item category", false, "ALL", "FABRIC", "ACCESSORIES", "TRIMS", "PACKING", "CHEMICALS", "OTHER") },
            Keywords: new[] { "stock register", "bin card", "ledger" },
            Fields: new[]
            {
                CT("itemCategory", "Item category"), C("receipts", "Received (qty)"), C("issues", "Issued (qty)"),
                GTbl("rows", "Movements", CT("date", "Date"), CT("itemCode", "Item"), CT("itemName", "Name"), CT("document", "Document"), CT("counterparty", "From / to"),
                    C("received", "In"), C("issued", "Out"), C("balance", "Balance")),
                Remarks,
            }),
        new("store.stock_balance", "Stock Balance Report", DocumentCategories.Store,
            "Balance of every store item at the end of a day, with items below their reorder level.",
            DocumentRisks.Low, ApprovalLevels.None, PeriodKind.Day, Generated: true, DefaultAutoGenerate: false,
            Keywords: new[] { "stock balance", "closing stock", "inventory balance" },
            Fields: new[]
            {
                C("items", "Items", FieldKind.Integer), C("belowReorder", "Below reorder level", FieldKind.Integer),
                GTbl("rows", "Items", CT("itemCode", "Item"), CT("itemName", "Name"), CT("category", "Category"), CT("unit", "Unit"), C("received", "Received"),
                    C("issued", "Issued"), C("balance", "Balance"), C("rolls", "Rolls in stock", FieldKind.Integer), C("reorderLevel", "Reorder level"), CT("flag", "Flag")),
                Remarks,
            }),

        // ── Quality ─────────────────────────────────────────────────────────
        Qc("qc.inline", "Inline Inspection Report", "Inline", new[] { "inline inspection", "in-line", "roving" }),
        Qc("qc.endline", "End-line Inspection Report", "EndOfLine", new[] { "end line", "end-line", "endline" }),
        Qc("qc.final", "Final Inspection Report", "Final", new[] { "final inspection", "pre-shipment", "fri" }),
        new("qc.defect", "Defect Report", DocumentCategories.Quality,
            "Defects accepted by inspectors in a period, by category and severity, with defects per hundred units.",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.Range, Generated: true, LineOption: true,
            Keywords: new[] { "defect", "dhu", "defect analysis" },
            Fields: new[]
            {
                C("inspections", "Inspections", FieldKind.Integer), C("defects", "Defects", FieldKind.Integer), C("dhu", "Defects per 100 units", unit: "%"),
                GTbl("rows", "Defects by category", CT("category", "Category"), C("critical", "Critical", FieldKind.Integer), C("major", "Major", FieldKind.Integer),
                    C("minor", "Minor", FieldKind.Integer), C("total", "Total", FieldKind.Integer)),
                Remarks,
            }),
        new("qc.rework", "Rework Report", DocumentCategories.Quality,
            "Rework and rejects recorded with production output, and inspections decided as rework, in a period.",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.Range, Generated: true, LineOption: true,
            Keywords: new[] { "rework", "alter", "repair" },
            Fields: new[]
            {
                C("good", "Good pieces", FieldKind.Integer), C("rework", "Rework", FieldKind.Integer), C("reject", "Rejected", FieldKind.Integer), C("reworkRate", "Rework rate", unit: "%"),
                C("inspectionsRework", "Inspections decided rework", FieldKind.Integer),
                GTbl("rows", "By line and order", CT("line", "Line"), CT("order", "Order"), C("good", "Good", FieldKind.Integer), C("rework", "Rework", FieldKind.Integer),
                    C("reject", "Reject", FieldKind.Integer), C("reworkRate", "Rework %")),
                Remarks,
            }),
        new("qc.aql", "AQL Report", DocumentCategories.Quality,
            "Acceptance sampling (ISO 2859-1, single sampling, normal inspection): sample size and accept / reject numbers are calculated, any critical defect fails the lot.",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.None, Generated: false, LineOption: true,
            Keywords: new[] { "aql", "acceptance quality", "sampling", "lot size" },
            Fields: new[]
            {
                T("orderReference", "Order / PO", true, lookup: "merch_order"), T("buyer", "Buyer"), T("style", "Style"), D("inspectionDate", "Inspection date", true),
                I("lotSize", "Lot size (pcs)", true), S("inspectionLevel", "Inspection level", true, "I", "II", "III"),
                S("aqlMajor", "AQL major", true, "1.0", "1.5", "2.5", "4.0", "6.5"), S("aqlMinor", "AQL minor", true, "1.0", "1.5", "2.5", "4.0", "6.5"),
                I("criticalFound", "Critical defects found", true), I("majorFound", "Major defects found", true), I("minorFound", "Minor defects found", true),
                CT("codeLetter", "Sample size code letter"), C("sampleSize", "Sample size", FieldKind.Integer),
                C("majorAccept", "Major Ac", FieldKind.Integer), C("majorReject", "Major Re", FieldKind.Integer), C("minorAccept", "Minor Ac", FieldKind.Integer), C("minorReject", "Minor Re", FieldKind.Integer),
                CT("result", "Result"), T("inspector", "Inspector", true), Remarks,
            }),
        new("qc.capa", "CAPA Report", DocumentCategories.Quality,
            "Corrective and preventive action for a quality problem.",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.None, Generated: false, LineOption: true,
            Keywords: new[] { "capa", "corrective action", "preventive action", "root cause" },
            Fields: new[]
            {
                A("problem", "Problem", true), S("source", "Source", true, "INSPECTION", "AUDIT", "CUSTOMER_COMPLAINT", "INTERNAL"), T("reference", "Related reference"),
                A("rootCause", "Root cause", true), A("correctiveAction", "Corrective action", true), A("preventiveAction", "Preventive action", true),
                T("owner", "Responsible person", true), D("dueDate", "Due date", true), S("status", "Status", true, "OPEN", "IN_PROGRESS", "CLOSED"), A("verification", "Verification of effectiveness"),
            }),

        // ── Merchandising ───────────────────────────────────────────────────
        new("merch.po", "Purchase Order (PO)", DocumentCategories.Merchandising,
            "The buyer's purchase order, filled from the buyer order when it exists; totals are calculated.",
            DocumentRisks.High, ApprovalLevels.ReviewAndApprove, PeriodKind.None, Generated: false, NumberField: "poNumber",
            Keywords: new[] { "purchase order", "p.o.", "po no", "order confirmation" },
            Fields: new[]
            {
                T("buyer", "Buyer", true), T("poNumber", "PO number", true, lookup: "merch_order"), T("style", "Style"), T("description", "Product description"),
                I("quantity", "Order quantity", true), S("unit", "Unit", false, "pcs", "dozen", "sets", "packs"), N("unitPrice", "Unit price"), T("currency", "Currency"),
                D("deliveryDate", "Delivery date", true), T("destination", "Destination"),
                Tbl("breakdown", "Colour × size breakdown", false, T("color", "Colour", true), T("size", "Size", true), I("quantity", "Quantity", true)),
                C("breakdownTotal", "Breakdown total", FieldKind.Integer), C("totalValue", "Order value"), Remarks,
            }),
        new("merch.order_sheet", "Order Sheet", DocumentCategories.Merchandising,
            "Internal order sheet for production: style, fabric, colours, sizes and delivery, filled from the buyer order.",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.None, Generated: false, NumberField: "poNumber",
            Keywords: new[] { "order sheet", "order summary", "production order sheet" },
            Fields: new[]
            {
                T("buyer", "Buyer", true), T("poNumber", "PO number", true, lookup: "merch_order"), T("style", "Style", true), T("description", "Description"),
                I("quantity", "Order quantity", true), D("deliveryDate", "Delivery date", true), T("fabric", "Fabric"), N("gsm", "GSM"), T("sizeRange", "Size range"),
                Tbl("breakdown", "Colour × size breakdown", false, T("color", "Colour", true), T("size", "Size", true), I("quantity", "Quantity", true)),
                C("breakdownTotal", "Breakdown total", FieldKind.Integer), A("specialInstructions", "Special instructions"),
            }),
        new("merch.bom", "Bill of Materials (BOM)", DocumentCategories.Merchandising,
            "Materials of a style with consumption and wastage; required quantities and costs are calculated for the order quantity.",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.None, Generated: false,
            Keywords: new[] { "bill of material", "bom", "consumption" },
            Fields: new[]
            {
                T("style", "Style", true), T("buyer", "Buyer"), T("poNumber", "PO number", lookup: "merch_order"), I("orderQuantity", "Order quantity (pcs)", true),
                Tbl("items", "Materials", true, T("material", "Material", true), S("type", "Type", false, "FABRIC", "ACCESSORIES", "TRIMS", "PACKING", "OTHER"), T("unit", "Unit", true),
                    N("consumption", "Consumption / pc", true), N("wastagePct", "Wastage %"), C("requiredQty", "Required qty"), N("unitCost", "Unit cost"), C("totalCost", "Total cost")),
                C("materialCost", "Material cost (order)"), C("costPerPiece", "Material cost / pc"), Remarks,
            }),
        new("merch.costing", "Costing Sheet", DocumentCategories.Merchandising,
            "Cost build-up of a style (per piece or per dozen) with commercial cost and margin; FOB is calculated and checked against the quoted price.",
            DocumentRisks.High, ApprovalLevels.ReviewAndApprove, PeriodKind.None, Generated: false,
            Keywords: new[] { "costing", "cost sheet", "fob", "cm cost" },
            Fields: new[]
            {
                T("style", "Style", true), T("buyer", "Buyer"), T("currency", "Currency", true), S("basis", "Costing basis", true, "PER_PIECE", "PER_DOZEN"),
                Tbl("lines", "Cost lines", true, T("component", "Component", true), S("category", "Category", false, "FABRIC", "TRIMS", "CM", "WASHING", "PRINT_EMBROIDERY", "TESTING", "FREIGHT", "OVERHEAD", "OTHER"),
                    N("amount", "Amount", true)),
                N("commercialPct", "Commercial cost %"), N("marginPct", "Margin %"), N("quotedPrice", "Quoted price"),
                C("totalCost", "Total cost"), C("commercialCost", "Commercial cost"), C("fobPrice", "FOB price"), C("fobPerPiece", "FOB per piece"), Remarks,
            }),
        new("merch.buyer_instruction", "Buyer Instruction Sheet", DocumentCategories.Merchandising,
            "The buyer's labelling, packing, quality and shipping instructions for an order.",
            DocumentRisks.Low, ApprovalLevels.Single, PeriodKind.None, Generated: false,
            Keywords: new[] { "buyer instruction", "packing instruction", "labeling instruction", "shipping mark" },
            Fields: new[]
            {
                T("buyer", "Buyer", true), T("poNumber", "PO number", lookup: "merch_order"), T("style", "Style"),
                Tbl("instructions", "Instructions", true, S("area", "Area", true, "LABELING", "PACKING", "QUALITY", "SHIPPING", "OTHER"), T("instruction", "Instruction", true)),
                Remarks,
            }),

        // ── Compliance ──────────────────────────────────────────────────────
        new("compliance.audit", "Compliance Audit Report", DocumentCategories.Compliance,
            "Findings of a social, environmental, buyer or fire / electrical audit with corrective actions.",
            DocumentRisks.High, ApprovalLevels.ReviewAndApprove, PeriodKind.None, Generated: false,
            Keywords: new[] { "compliance audit", "social audit", "bsci", "wrap", "sedex", "audit report" },
            Fields: new[]
            {
                S("auditType", "Audit type", true, "SOCIAL", "ENVIRONMENTAL", "BUYER", "INTERNAL", "FIRE_ELECTRICAL"), T("auditor", "Auditor / body", true), D("auditDate", "Audit date", true), T("scope", "Scope"),
                Tbl("findings", "Findings", false, T("area", "Area"), T("finding", "Finding", true), S("severity", "Severity", true, "CRITICAL", "MAJOR", "MINOR", "OBSERVATION"),
                    T("correctiveAction", "Corrective action"), T("owner", "Owner"), D("dueDate", "Due"), S("status", "Status", false, "OPEN", "CLOSED")),
                C("critical", "Critical", FieldKind.Integer), C("major", "Major", FieldKind.Integer), C("minor", "Minor", FieldKind.Integer), C("open", "Open findings", FieldKind.Integer),
                S("overallResult", "Overall result", true, "PASS", "CONDITIONAL", "FAIL"), A("summary", "Summary"),
            }),
        new("compliance.safety", "Safety Inspection Report", DocumentCategories.Compliance,
            "Fire, electrical, building and workplace safety checklist of an area.",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.None, Generated: false,
            Keywords: new[] { "safety inspection", "fire safety", "electrical safety", "checklist" },
            Fields: new[]
            {
                D("inspectionDate", "Inspection date", true), T("inspector", "Inspector", true), T("area", "Area / floor", true),
                Tbl("checklist", "Checklist", true, T("item", "Item", true), S("result", "Result", true, "OK", "NOT_OK", "NA"), T("remark", "Remark")),
                C("notOk", "Items not OK", FieldKind.Integer), A("actionsRequired", "Actions required"),
            }),
        new("compliance.training", "Training Record", DocumentCategories.Compliance,
            "A training session and its attendees (names filled from the worker directory).",
            DocumentRisks.Low, ApprovalLevels.None, PeriodKind.None, Generated: false,
            Keywords: new[] { "training record", "training attendance", "awareness training" },
            Fields: new[]
            {
                T("title", "Training", true), T("trainer", "Trainer", true), D("trainingDate", "Date", true), N("durationHours", "Duration (hours)", true), T("topic", "Topics"),
                Tbl("attendees", "Attendees", true, T("workerCode", "Worker ID", true, lookup: "worker"), CT("name", "Name"), B("signedOff", "Signed")),
                C("attendeeCount", "Attendees", FieldKind.Integer), Remarks,
            }),

        // ── Commercial ──────────────────────────────────────────────────────
        new("commercial.invoice", "Commercial Invoice", DocumentCategories.Commercial,
            "Invoice to the buyer; amounts and totals are calculated and checked against the buyer order and the packing list.",
            DocumentRisks.High, ApprovalLevels.ReviewAndApprove, PeriodKind.None, Generated: false, NumberField: "invoiceNo",
            Keywords: new[] { "commercial invoice", "invoice no", "invoice" },
            Fields: new[]
            {
                T("invoiceNo", "Invoice no.", true), D("invoiceDate", "Invoice date", true), T("buyer", "Buyer", true), T("poNumber", "PO number", true, lookup: "merch_order"),
                T("currency", "Currency", true), T("lcNumber", "L/C number"), T("paymentTerms", "Payment terms"), T("incoterm", "Incoterm"), T("destination", "Destination"),
                Tbl("lines", "Lines", true, T("description", "Description", true), T("style", "Style"), I("quantity", "Quantity", true), T("unit", "Unit"),
                    N("unitPrice", "Unit price", true), C("amount", "Amount")),
                C("totalQuantity", "Total quantity", FieldKind.Integer), C("totalAmount", "Total amount"), Remarks,
            }),
        new("commercial.packing_list", "Packing List", DocumentCategories.Commercial,
            "Cartons of a shipment by range, colour and size; carton and piece counts and weights are totalled and checked against the invoice.",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.None, Generated: false, NumberField: "packingListNo",
            Keywords: new[] { "packing list", "carton", "ctn no" },
            Fields: new[]
            {
                T("packingListNo", "Packing list no.", true), T("invoiceNo", "Invoice no.", true), T("buyer", "Buyer"), T("poNumber", "PO number", lookup: "merch_order"),
                Tbl("cartons", "Cartons", true, I("cartonFrom", "Carton from", true), I("cartonTo", "Carton to", true), T("color", "Colour"), T("size", "Size"),
                    I("piecesPerCarton", "Pcs / carton", true), N("netKg", "Net kg / carton"), N("grossKg", "Gross kg / carton"), C("cartons", "Cartons", FieldKind.Integer), C("pieces", "Pieces", FieldKind.Integer)),
                C("totalCartons", "Total cartons", FieldKind.Integer), C("totalPieces", "Total pieces", FieldKind.Integer), C("totalNetKg", "Total net kg"), C("totalGrossKg", "Total gross kg"),
                Remarks,
            }),
        new("commercial.shipment", "Shipment Documents", DocumentCategories.Commercial,
            "Bill of lading / airway bill details of a shipment, linked to its invoice and packing list.",
            DocumentRisks.High, ApprovalLevels.Single, PeriodKind.None, Generated: false, NumberField: "blNumber",
            Keywords: new[] { "bill of lading", "airway bill", "awb", "b/l", "shipment" },
            Fields: new[]
            {
                T("invoiceNo", "Invoice no.", true), T("packingListNo", "Packing list no.", true), S("mode", "Mode", true, "SEA", "AIR", "ROAD"), T("blNumber", "B/L or AWB no.", true),
                T("carrier", "Vessel / flight"), T("containerNo", "Container no."), T("portOfLoading", "Port of loading"), T("portOfDischarge", "Port of discharge"),
                D("etd", "ETD", true), D("eta", "ETA"), T("forwarder", "Forwarder"), Remarks,
            }),

        // ── Devices ─────────────────────────────────────────────────────────
        new("devices.log", "Device Log Report", DocumentCategories.Devices,
            "Device health of a period from the device event history: events, errors, warnings and offline periods per device.",
            DocumentRisks.Low, ApprovalLevels.None, PeriodKind.Range, Generated: true,
            Keywords: new[] { "device log", "system log", "heartbeat" },
            Fields: new[]
            {
                C("devices", "Devices", FieldKind.Integer), C("events", "Events", FieldKind.Integer), C("errors", "Errors", FieldKind.Integer), C("offline", "Went offline", FieldKind.Integer),
                GTbl("devicesTable", "By device", CT("device", "Device"), CT("category", "Category"), C("events", "Events", FieldKind.Integer), C("errors", "Errors", FieldKind.Integer),
                    C("warnings", "Warnings", FieldKind.Integer), C("offline", "Offline", FieldKind.Integer), CT("simulated", "Simulated")),
                GTbl("issues", "Errors and warnings", CT("at", "When"), CT("device", "Device"), CT("type", "Event"), CT("severity", "Severity"), CT("message", "Message")),
                Remarks,
            }),
        new("devices.maintenance", "Maintenance Record", DocumentCategories.Devices,
            "Preventive or breakdown maintenance of a machine, filled from the machine register.",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.None, Generated: false,
            Keywords: new[] { "maintenance", "machine repair", "service record" },
            Fields: new[]
            {
                T("machineCode", "Machine (asset tag)", true, lookup: "machine"), CT("machineType", "Machine type"), CT("brand", "Brand"), CT("serialNumber", "Serial no."),
                S("maintenanceType", "Type", true, "PREVENTIVE", "BREAKDOWN", "CALIBRATION"), D("maintenanceDate", "Date", true), I("downtimeMinutes", "Downtime (min)"),
                T("technician", "Technician", true), A("problem", "Problem"), A("actionTaken", "Action taken", true),
                Tbl("parts", "Parts replaced", false, T("part", "Part", true), N("quantity", "Qty", true), N("cost", "Cost")), C("partsCost", "Parts cost"),
                D("nextDueDate", "Next due"),
            }),
    };

    private static DocumentTypeDef Stage(string code, string name, string description, string[] stages, string[] keywords)
    {
        var cols = new List<FieldDef> { CT("line", "Line"), CT("order", "Order"), CT("buyer", "Buyer") };
        cols.AddRange(stages.Select(s => C(s, StageLabel(s), FieldKind.Integer)));
        cols.Add(C("rework", "Rework", FieldKind.Integer));
        cols.Add(C("reject", "Reject", FieldKind.Integer));
        var fields = new List<FieldDef>();
        fields.AddRange(stages.Select(s => C(s, StageLabel(s) + " (pcs)", FieldKind.Integer)));
        fields.Add(C("rework", "Rework (pcs)", FieldKind.Integer));
        fields.Add(C("reject", "Rejected (pcs)", FieldKind.Integer));
        fields.Add(GTbl("rows", "By line and order", cols.ToArray()));
        fields.Add(Remarks);
        return new DocumentTypeDef(code, name, DocumentCategories.Production, description + " From the recorded production output of one day.",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.Day, Generated: true, fields, LineOption: true, Keywords: keywords);
    }

    public static string StageLabel(string stage) => stage switch
    {
        "cutting" => "Cut", "sewingInput" => "Sewing input", "sewingOutput" => "Sewing output", "finishing" => "Finished", "packing" => "Packed", _ => stage,
    };

    public static IReadOnlyList<string> Stages => StageColumns;

    private static DocumentTypeDef Qc(string code, string name, string stage, string[] keywords) =>
        new(code, name, DocumentCategories.Quality,
            $"{name.Replace(" Report", "")}s of one day from the QC inspections ({stage} stage): outcomes, accepted defects by severity, pending reviews.",
            DocumentRisks.Normal, ApprovalLevels.Single, PeriodKind.Day, Generated: true, LineOption: true, Keywords: keywords,
            Fields: new[]
            {
                C("inspections", "Inspections", FieldKind.Integer), C("pass", "Pass", FieldKind.Integer), C("rework", "Rework", FieldKind.Integer), C("reject", "Reject", FieldKind.Integer),
                C("pendingReview", "Not yet reviewed", FieldKind.Integer), C("defects", "Accepted defects", FieldKind.Integer),
                GTbl("rows", "Inspections", CT("reference", "Reference"), CT("line", "Line"), CT("order", "Order"), CT("product", "Product"), CT("outcome", "Outcome"),
                    C("critical", "Critical", FieldKind.Integer), C("major", "Major", FieldKind.Integer), C("minor", "Minor", FieldKind.Integer), CT("reviewedBy", "Reviewed by")),
                Remarks,
            });

    /// <summary>QC stage of a QC generator type.</summary>
    public static string? QcStage(string code) => code switch { "qc.inline" => "Inline", "qc.endline" => "EndOfLine", "qc.final" => "Final", _ => null };

    private static readonly Dictionary<string, DocumentTypeDef> ByCode = All.ToDictionary(t => t.Code, StringComparer.Ordinal);

    public static DocumentTypeDef? Get(string? code) => code is null ? null : ByCode.GetValueOrDefault(code);

    /// <summary>Field (top level) of a type.</summary>
    public static FieldDef? Field(DocumentTypeDef type, string key) => type.Fields.FirstOrDefault(f => f.Key == key);

    /// <summary>The approval level actually used: the factory's setting, else the type default; HIGH risk never goes without approval.</summary>
    public static string EffectiveApproval(DocumentTypeDef type, string? setting)
    {
        var level = setting is not null && ApprovalLevels.All.Contains(setting) ? setting : type.DefaultApproval;
        return type.Risk == DocumentRisks.High && level == ApprovalLevels.None ? ApprovalLevels.Single : level;
    }
}
