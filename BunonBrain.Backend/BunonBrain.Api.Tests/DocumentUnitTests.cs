using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Documents;
using BunonBrain.Application.Settings;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BunonBrain.Api.Tests;

/// <summary>
/// The document engine without a database: the type catalog, deterministic
/// calculations (AQL per ISO 2859-1, totals, costing, cartons), validation and
/// duplicate keys, sanitising of untrusted input, rule-based extraction and
/// the grounding check of AI values, classification, the PDF / XLSX / DOCX
/// writers, and the AI client's JSON handling, retries and limits.
/// </summary>
public sealed class DocumentUnitTests
{
    private static readonly DateOnly Today = new(2026, 11, 16);
    private static DocumentTypeDef Type(string code) => DocumentCatalog.Get(code)!;
    private static JsonObject J(string json) => (JsonObject)JsonNode.Parse(json)!;

    // ─── Catalog ───────────────────────────────────────────────────────────

    [Fact]
    public void Catalog_covers_every_department_with_unique_codes_and_valid_settings()
    {
        Assert.Equal(DocumentCatalog.All.Count, DocumentCatalog.All.Select(t => t.Code).Distinct().Count());
        Assert.True(DocumentCatalog.All.Count >= 38);
        foreach (var t in DocumentCatalog.All)
        {
            Assert.Contains(DocumentCategories.All, c => c.Code == t.Category);
            Assert.Contains(t.DefaultApproval, ApprovalLevels.All);
            Assert.StartsWith(t.Category == "quality" ? "qc." : t.Category == "merchandising" ? "merch." : t.Category + ".", t.Code);
            Assert.Equal(t.Fields.Count, t.Fields.Select(f => f.Key).Distinct().Count());
            Assert.All(t.Fields.Where(f => f.Kind == FieldKind.Table), f => Assert.NotEmpty(f.Columns!));
            if (t.NumberField is { } nf) Assert.NotNull(DocumentCatalog.Field(t, nf));
            // High-risk documents can't be filed without an approver, whatever a factory configures.
            if (t.Risk == DocumentRisks.High) Assert.NotEqual(ApprovalLevels.None, DocumentCatalog.EffectiveApproval(t, ApprovalLevels.None));
        }
        foreach (var cat in DocumentCategories.All) Assert.Contains(DocumentCatalog.All, t => t.Category == cat.Code);
        foreach (var required in new[] { "production.daily", "production.line", "production.cutting", "production.sewing", "production.finishing", "production.wip", "production.downtime",
                     "hr.attendance", "hr.late_absent", "hr.overtime", "hr.leave", "hr.employee", "hr.payroll_input", "store.grn", "store.requisition", "store.issue",
                     "store.stock_register", "store.stock_balance", "qc.inline", "qc.endline", "qc.final", "qc.defect", "qc.rework", "qc.aql", "qc.capa",
                     "merch.po", "merch.order_sheet", "merch.bom", "merch.costing", "merch.buyer_instruction", "compliance.audit", "compliance.safety",
                     "compliance.training", "commercial.invoice", "commercial.packing_list", "commercial.shipment" })
            Assert.NotNull(DocumentCatalog.Get(required));
    }

    [Fact]
    public void Generated_types_hold_only_computed_figures_plus_remarks()
    {
        foreach (var t in DocumentCatalog.All.Where(t => t.Generated))
        {
            Assert.True(DocumentGenerators.Supports(t.Code));
            Assert.All(t.Fields.Where(f => !f.Computed), f => Assert.Equal(FieldKind.TextArea, f.Kind));
            Assert.NotEqual(PeriodKind.None, t.Period);
        }
    }

    [Fact]
    public void Document_permissions_are_used_and_categories_map_to_permissions()
    {
        foreach (var c in DocumentCategories.All) Assert.NotNull(AccessCatalog.Permission(c.Permission));
        foreach (var p in new[] { Permissions.DocumentsView, Permissions.DocumentsCreate, Permissions.DocumentsApprove, Permissions.NotificationsView, Permissions.InventoryView })
            Assert.False(AccessCatalog.Permission(p)!.Reserved);
        var store = new AccessContext(Guid.NewGuid(), "s@x", new[] { SystemRoles.StoreOfficer }, AccessCatalog.Role(SystemRoles.StoreOfficer)!.Permissions, new[] { Guid.NewGuid() }, Array.Empty<string>());
        Assert.Equal(new[] { DocumentCategories.Store }, DocumentCategories.For(store));
        var controller = new AccessContext(Guid.NewGuid(), "c@x", new[] { SystemRoles.DocumentController }, AccessCatalog.Role(SystemRoles.DocumentController)!.Permissions, new[] { Guid.NewGuid() }, Array.Empty<string>());
        Assert.Equal(DocumentCategories.All.Count, DocumentCategories.For(controller).Count);
    }

    // ─── AQL ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1000, "II", "2.5", 'J', 80, 5)]
    [InlineData(1000, "II", "4.0", 'J', 80, 7)]
    [InlineData(3000, "II", "2.5", 'K', 125, 7)]
    [InlineData(3000, "II", "4.0", 'K', 125, 10)]
    [InlineData(3000, "II", "1.5", 'K', 125, 5)]
    [InlineData(500, "II", "2.5", 'H', 50, 3)]
    [InlineData(5000, "III", "2.5", 'M', 315, 14)]
    [InlineData(200, "I", "4.0", 'E', 13, 1)]
    public void Aql_plans_follow_iso_2859_1(int lot, string level, string aql, char letter, int sample, int accept)
    {
        var plan = AqlCalculator.Plan(lot, level, aql)!;
        Assert.Equal(letter, plan.CodeLetter);
        Assert.Equal(sample, plan.SampleSize);
        Assert.Equal(accept, plan.Accept);
        Assert.Equal(accept + 1, plan.Reject);
    }

    [Fact]
    public void Aql_arrows_move_to_the_next_plan_and_the_sample_never_exceeds_the_lot()
    {
        // Lot 40, level II → D (8); AQL 2.5 has ↑ at D → C: 5 pieces, Ac 0.
        var up = AqlCalculator.Plan(40, "II", "2.5")!;
        Assert.Equal(('C', 5, 0), (up.CodeLetter, up.SampleSize, up.Accept));
        // Lot 60, level II → E (13); AQL 2.5 has ↓ at E → F: 20 pieces, Ac 1.
        var down = AqlCalculator.Plan(60, "II", "2.5")!;
        Assert.Equal(('F', 20, 1), (down.CodeLetter, down.SampleSize, down.Accept));
        // Lot 10, level I → A; AQL 1.0 goes down to E (13 pieces) but the lot has only 10.
        Assert.Equal(10, AqlCalculator.Plan(10, "I", "1.0")!.SampleSize);
        Assert.Null(AqlCalculator.Plan(1, "II", "2.5"));
        Assert.Null(AqlCalculator.Plan(100, "IV", "2.5"));
        Assert.Null(AqlCalculator.Plan(100, "II", "0.65"));
    }

    [Fact]
    public void Aql_report_passes_or_fails_and_any_critical_defect_fails()
    {
        var d = J("""{"orderReference":"PO-1","inspectionDate":"2026-11-15","lotSize":1000,"inspectionLevel":"II","aqlMajor":"2.5","aqlMinor":"4.0","criticalFound":0,"majorFound":5,"minorFound":7,"inspector":"QA"}""");
        var c = DocumentRules.Validate(Type("qc.aql"), d, Today);
        Assert.True(c.CanSubmit);
        Assert.Equal("J", (string?)d["codeLetter"]);
        Assert.Equal(80, (int)d["sampleSize"]!);
        Assert.Equal("PASS", (string?)d["result"]);
        d["majorFound"] = 6;
        DocumentRules.Validate(Type("qc.aql"), d, Today);
        Assert.Equal("FAIL", (string?)d["result"]);
        d["majorFound"] = 0; d["criticalFound"] = 1;
        var c2 = DocumentRules.Validate(Type("qc.aql"), d, Today);
        Assert.Equal("FAIL", (string?)d["result"]);
        Assert.Contains(c2.Warnings, w => w.Code == "critical_found");
    }

    // ─── Calculations ──────────────────────────────────────────────────────

    [Fact]
    public void Grn_amounts_and_totals_are_calculated_and_quantities_must_add_up()
    {
        var d = J("""{"supplier":"ABC Textiles","challanNo":"CH-77","receivedOn":"2026-11-15","items":[{"itemCode":"FAB-01","itemName":"Single jersey","unit":"kg","receivedQty":100,"acceptedQty":95,"rejectedQty":5,"unitCost":3.5,"amount":999},{"itemCode":"BTN-01","itemName":"Button","unit":"pcs","receivedQty":1000,"acceptedQty":900,"rejectedQty":50}]}""");
        var c = DocumentRules.Validate(Type("store.grn"), d, Today);
        var rows = (JsonArray)d["items"]!;
        Assert.Equal(332.5m, (decimal)rows[0]!["amount"]!);  // typed 999 is overwritten
        Assert.Null(rows[1]!["amount"]);                       // no unit cost: no amount, not zero
        Assert.Equal(995m, (decimal)d["totalAccepted"]!);
        Assert.Equal(332.5m, (decimal)d["totalAmount"]!);
        Assert.Contains(c.Errors, e => e.Code == "quantities_dont_add_up" && e.Field == "items[1].rejectedQty");
        Assert.False(c.CanSubmit);
    }

    [Fact]
    public void Grn_rolls_must_add_up_to_the_accepted_quantity_and_each_roll_is_listed_once()
    {
        static JsonObject Grn(string rolls) => J($$"""{"supplier":"ABC","challanNo":"CH-1","receivedOn":"2026-11-15","items":[{"itemCode":"FAB-01","itemName":"Jersey","category":"FABRIC","unit":"m","receivedQty":100,"acceptedQty":100,"rejectedQty":0}],"rolls":{{rolls}}}""");
        var ok = Grn("""[{"itemCode":"fab-01","rollNumber":"R1","lotNumber":"L7","quantity":60},{"itemCode":"FAB-01","rollNumber":"R2","lotNumber":"L7","quantity":40}]""");
        var c = DocumentRules.Validate(Type("store.grn"), ok, Today);
        Assert.True(c.CanSubmit, string.Join(" ", c.Errors.Select(e => e.Message)));
        Assert.Equal(2m, (decimal)ok["rollCount"]!);

        var shortRolls = Grn("""[{"itemCode":"FAB-01","rollNumber":"R1","quantity":60}]""");
        Assert.Contains(DocumentRules.Validate(Type("store.grn"), shortRolls, Today).Errors, e => e.Code == "rolls_dont_add_up");

        var dup = Grn("""[{"itemCode":"FAB-01","rollNumber":"r1","lotNumber":"L7","quantity":50},{"itemCode":"FAB-01","rollNumber":"R1","lotNumber":"l7","quantity":50}]""");
        Assert.Contains(DocumentRules.Validate(Type("store.grn"), dup, Today).Errors, e => e.Code == "duplicate_roll");

        var other = Grn("""[{"itemCode":"BTN-9","rollNumber":"R1","quantity":100}]""");
        Assert.Contains(DocumentRules.Validate(Type("store.grn"), other, Today).Errors, e => e.Code == "roll_item_not_listed");

        var zero = Grn("""[{"itemCode":"FAB-01","rollNumber":"R1","quantity":0},{"itemCode":"FAB-01","rollNumber":"R2","quantity":100}]""");
        Assert.Contains(DocumentRules.Validate(Type("store.grn"), zero, Today).Errors, e => e.Code == "zero_quantity" && e.Field == "rolls[0].quantity");

        // Issue notes: the same roll twice is refused before any database check.
        var issue = J("""{"issuedTo":"Cutting","issuedOn":"2026-11-15","items":[{"itemCode":"FAB-01","quantity":10}],"rolls":[{"rollNumber":"R1","quantity":5},{"rollNumber":"R1","quantity":5}]}""");
        Assert.Contains(DocumentRules.Validate(Type("store.issue"), issue, Today).Errors, e => e.Code == "duplicate_roll");
    }

    // ─── Scheduling ────────────────────────────────────────────────────────

    [Fact]
    public void Schedules_generate_only_complete_periods_with_frequencies_the_type_allows()
    {
        var wed = new DateOnly(2026, 11, 18); // a Wednesday
        Assert.Equal((new DateOnly(2026, 11, 17), new DateOnly(2026, 11, 17)), DocumentSchedule.LatestCompletePeriod(ScheduleFrequencies.Daily, wed, 6));
        // Week starting Saturday: the last full week is Sat 7 Nov .. Fri 13 Nov (the current one began Sat 14 Nov).
        Assert.Equal((new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 13)), DocumentSchedule.LatestCompletePeriod(ScheduleFrequencies.Weekly, wed, 6));
        // On the first day of a week the week just ended is complete.
        Assert.Equal((new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 13)), DocumentSchedule.LatestCompletePeriod(ScheduleFrequencies.Weekly, new DateOnly(2026, 11, 14), 6));
        Assert.Equal((new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 15)), DocumentSchedule.LatestCompletePeriod(ScheduleFrequencies.Weekly, wed, 1)); // Monday weeks
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)), DocumentSchedule.LatestCompletePeriod(ScheduleFrequencies.Monthly, wed, 6));
        Assert.Equal((new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31)), DocumentSchedule.LatestCompletePeriod(ScheduleFrequencies.Monthly, new DateOnly(2026, 1, 1), 6));

        Assert.Equal(new[] { ScheduleFrequencies.Daily }, DocumentSchedule.Allowed(Type("hr.attendance")));
        Assert.Equal(new[] { ScheduleFrequencies.Weekly, ScheduleFrequencies.Monthly }, DocumentSchedule.Allowed(Type("hr.payroll_input")));
        Assert.Empty(DocumentSchedule.Allowed(Type("store.grn")));                       // forms are never generated
        Assert.Equal(ScheduleFrequencies.Weekly, DocumentSchedule.Frequency(Type("hr.overtime"), ScheduleFrequencies.Daily)); // not allowed → default
        Assert.Equal(ScheduleScopes.EachLine, DocumentSchedule.Scope(Type("production.line"), ScheduleScopes.Factory));      // only exists per line
        Assert.Equal(ScheduleScopes.Factory, DocumentSchedule.Scope(Type("production.daily"), ScheduleScopes.EachLine));     // factory-wide type
        Assert.Equal(ScheduleScopes.EachLine, DocumentSchedule.Scope(Type("hr.attendance"), ScheduleScopes.EachLine));
    }

    [Fact]
    public void Automation_is_off_unless_explicitly_enabled()
    {
        DocumentOptions Opt(string? v) => DocumentOptions.FromEnvironment(k => k == "DOCUMENT_AUTOMATION_ENABLED" ? v : null, Path.GetTempPath());
        Assert.False(Opt(null).AutomationEnabled);
        Assert.False(Opt("").AutomationEnabled);
        Assert.False(Opt("yes").AutomationEnabled);
        Assert.False(Opt("false").AutomationEnabled);
        Assert.True(Opt("true").AutomationEnabled);
    }

    // ─── Leave and holidays ────────────────────────────────────────────────

    [Fact]
    public void Approved_leave_and_holidays_are_excused_never_absent()
    {
        var factory = Guid.NewGuid();
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var d1 = new DateOnly(2026, 11, 10); var d2 = d1.AddDays(1); var d3 = d1.AddDays(2);
        var cal = new BunonBrain.Application.Attendance.AttendanceCalendar(
            new[] { new BunonBrain.Application.Attendance.ApprovedLeave(a, d1, d2, "SICK", "DOC-HR-1") },
            new[] { new BunonBrain.Application.Attendance.HolidayDay(factory, d2, "Victory Day", "PUBLIC") });
        var scheduled = new[] { (a, d1), (a, d2), (a, d3), (b, d1), (b, d2), (c, d3) };
        var (expected, excused) = cal.Apply(scheduled, x => x.Item1, x => x.Item2, _ => factory);
        // a: d1 leave, d2 holiday (holiday wins), d3 expected; b: d1 expected, d2 holiday; c: d3 expected.
        Assert.Equal(new[] { (a, d3), (b, d1), (c, d3) }, expected.ToArray());
        Assert.Equal(3, excused.Count);
        Assert.Contains(excused, e => e.WorkerId == a && e.Date == d1 && e.Kind == "LEAVE" && e.Reference == "DOC-HR-1" && e.Detail == "SICK");
        Assert.Contains(excused, e => e.WorkerId == a && e.Date == d2 && e.Kind == "HOLIDAY" && e.Detail == "Victory Day");
        Assert.Contains(excused, e => e.WorkerId == b && e.Date == d2 && e.Kind == "HOLIDAY");
        // A holiday of another factory doesn't excuse anyone here; leave follows the worker.
        var (exp2, exc2) = cal.Apply(scheduled, x => x.Item1, x => x.Item2, _ => Guid.NewGuid());
        Assert.Equal(2, exc2.Count);
        Assert.All(exc2, e => Assert.Equal("LEAVE", e.Kind));
        Assert.Equal(4, exp2.Count);
    }

    [Fact]
    public void Costing_builds_fob_from_cost_commercial_and_margin_and_flags_a_price_below_cost()
    {
        var d = J("""{"style":"TS-01","currency":"USD","basis":"PER_DOZEN","lines":[{"component":"Fabric","amount":24},{"component":"CM","amount":12}],"commercialPct":5,"marginPct":10,"quotedPrice":40}""");
        var c = DocumentRules.Validate(Type("merch.costing"), d, Today);
        Assert.Equal(36m, (decimal)d["totalCost"]!);
        Assert.Equal(1.8m, (decimal)d["commercialCost"]!);
        Assert.Equal(41.58m, (decimal)d["fobPrice"]!);
        Assert.Equal(3.465m, (decimal)d["fobPerPiece"]!);
        Assert.Contains(c.Warnings, w => w.Code == "below_target_margin");
        d["quotedPrice"] = 30;
        Assert.Contains(DocumentRules.Validate(Type("merch.costing"), d, Today).Warnings, w => w.Code == "below_cost");
    }

    [Fact]
    public void Packing_list_counts_cartons_and_pieces_and_refuses_overlaps_and_light_gross_weight()
    {
        var d = J("""{"packingListNo":"PL-1","invoiceNo":"INV-1","cartons":[{"cartonFrom":1,"cartonTo":10,"piecesPerCarton":24,"netKg":8,"grossKg":9},{"cartonFrom":11,"cartonTo":15,"piecesPerCarton":12,"netKg":5,"grossKg":4}]}""");
        var c = DocumentRules.Validate(Type("commercial.packing_list"), d, Today);
        Assert.Equal(15m, (decimal)d["totalCartons"]!);
        Assert.Equal(300m, (decimal)d["totalPieces"]!);
        Assert.Equal(105m, (decimal)d["totalNetKg"]!);
        Assert.Contains(c.Errors, e => e.Code == "gross_below_net");
        ((JsonArray)d["cartons"]!)[1]!["cartonFrom"] = 10;
        Assert.Contains(DocumentRules.Validate(Type("commercial.packing_list"), d, Today).Errors, e => e.Code == "overlapping_cartons");
    }

    [Fact]
    public void Invoice_bom_leave_and_audit_calculations()
    {
        var inv = J("""{"lines":[{"description":"Tee","quantity":1200,"unitPrice":2.15},{"description":"Polo","quantity":300,"unitPrice":4}]}""");
        DocumentRules.Compute(Type("commercial.invoice"), inv);
        Assert.Equal(1500m, (decimal)inv["totalQuantity"]!);
        Assert.Equal(3780m, (decimal)inv["totalAmount"]!);

        var bom = J("""{"orderQuantity":1000,"items":[{"material":"Fabric","unit":"kg","consumption":0.25,"wastagePct":4,"unitCost":3}]}""");
        DocumentRules.Compute(Type("merch.bom"), bom);
        Assert.Equal(260m, (decimal)((JsonArray)bom["items"]!)[0]!["requiredQty"]!);
        Assert.Equal(780m, (decimal)bom["materialCost"]!);

        var leave = J("""{"workerCode":"W-001","leaveType":"SICK","fromDate":"2026-11-10","toDate":"2026-11-12","reason":"Fever"}""");
        Assert.True(DocumentRules.Validate(Type("hr.leave"), leave, Today).CanSubmit);
        Assert.Equal(3m, (decimal)leave["days"]!);
        leave["toDate"] = "2026-11-09";
        Assert.Contains(DocumentRules.Validate(Type("hr.leave"), leave, Today).Errors, e => e.Code == "date_order");

        var audit = J("""{"auditType":"SOCIAL","auditor":"X","auditDate":"2026-11-01","overallResult":"PASS","findings":[{"finding":"Blocked exit","severity":"CRITICAL","status":"OPEN"}]}""");
        var c = DocumentRules.Validate(Type("compliance.audit"), audit, Today);
        Assert.Equal(1m, (decimal)audit["critical"]!);
        Assert.Contains(c.Warnings, w => w.Code == "pass_with_critical");
        Assert.Contains(c.Missing, m => m.Field == "findings[0].correctiveAction");
    }

    // ─── Validation, sanitising, duplicates ────────────────────────────────

    [Fact]
    public void Missing_required_values_block_submission_and_malformed_values_are_errors()
    {
        var c = DocumentRules.Validate(Type("commercial.invoice"), J("""{"invoiceNo":"INV-9","invoiceDate":"16/11/2026","currency":"USD","lines":[]}"""), Today);
        Assert.False(c.CanSubmit);
        Assert.Contains(c.Missing, m => m.Field == "buyer");
        Assert.Contains(c.Missing, m => m.Field == "lines");
        Assert.Contains(c.Errors, e => e.Field == "invoiceDate" && e.Code == "not_a_date");
        var capa = DocumentRules.Validate(Type("qc.capa"), J("""{"problem":"p","source":"SOMETHING","rootCause":"r","correctiveAction":"c","preventiveAction":"p","owner":"o","dueDate":"2026-11-01","status":"OPEN"}"""), Today);
        Assert.Contains(capa.Errors, e => e.Code == "not_an_option");
        Assert.Contains(capa.Warnings, w => w.Code == "overdue");
    }

    [Fact]
    public void Sanitize_keeps_only_defined_fields_drops_computed_ones_and_coerces_types()
    {
        var dropped = new List<string>();
        var clean = DocumentRules.Sanitize(Type("store.grn"), J("""{"supplier":" ABC ","totalAmount":5,"__proto__":1,"receivedOn":"2026-11-15T10:00:00Z","items":[{"itemCode":"A","receivedQty":"1,200.5","acceptedQty":"x","amount":3,"evil":"<script>"}]}"""), keepComputed: false, dropped);
        Assert.Equal("ABC", (string?)clean["supplier"]);
        Assert.False(clean.ContainsKey("totalAmount"));
        Assert.False(clean.ContainsKey("__proto__"));
        Assert.Contains("__proto__", dropped);
        Assert.Equal("2026-11-15", (string?)clean["receivedOn"]);
        var row = (JsonObject)((JsonArray)clean["items"]!)[0]!;
        Assert.Equal(1200.5m, (decimal)row["receivedQty"]!);
        Assert.Equal("x", (string?)row["acceptedQty"]);   // kept as typed so validation names it
        Assert.False(row.ContainsKey("amount"));
        Assert.False(row.ContainsKey("evil"));
    }

    [Fact]
    public void Duplicate_keys_use_the_document_number_or_the_period_and_line()
    {
        Assert.Equal("commercial.invoice|INV-9", DocumentRules.DedupKey(Type("commercial.invoice"), J("""{"invoiceNo":"inv-9"}"""), null, null, null));
        Assert.Equal("store.grn|CH-1|ABC", DocumentRules.DedupKey(Type("store.grn"), J("""{"challanNo":"ch-1","supplier":"abc"}"""), null, null, null));
        Assert.Null(DocumentRules.DedupKey(Type("commercial.invoice"), new JsonObject(), null, null, null));
        var day = new DateOnly(2026, 11, 15);
        Assert.Equal("production.daily|2026-11-15|2026-11-15|*", DocumentRules.DedupKey(Type("production.daily"), new JsonObject(), day, day, null));
        Assert.Equal("production.line|2026-11-15|2026-11-15|line-2", DocumentRules.DedupKey(Type("production.line"), new JsonObject(), day, day, "line-2"));
        Assert.Equal("store.stock_register|2026-11-01|2026-11-15|*|FABRIC",
            DocumentRules.DedupKey(Type("store.stock_register"), J("""{"itemCategory":"FABRIC"}"""), new DateOnly(2026, 11, 1), day, null));
    }

    [Fact]
    public void Checks_round_trip_through_json()
    {
        var c = new DocumentChecks();
        c.Miss("a", "A"); c.Error("b", "e1", "bad"); c.Warn("c", "w1", "careful");
        var back = DocumentChecks.FromJson(c.ToJson());
        Assert.Equal(("a", "missing"), (back.Missing[0].Field, back.Missing[0].Code));
        Assert.Equal("bad", back.Errors[0].Message);
        Assert.Equal("w1", back.Warnings[0].Code);
        Assert.Empty(DocumentChecks.FromJson("not json").Missing);
    }

    // ─── Extraction and classification ─────────────────────────────────────

    [Fact]
    public void Rules_extraction_reads_labelled_values_and_header_tables()
    {
        var text = "COMMERCIAL INVOICE\nInvoice No: INV-2026-044\nInvoice date: 2026-11-14\nBuyer: Nordic Wear AB\nPO number: PO-7781\nCurrency: USD\n\n" +
                   "Description\tStyle\tQuantity\tUnit price\nCrew neck tee\tTS-01\t1200\t2.15\nPolo\tPL-02\t300\t4.00\n\nTotal 3780";
        var methods = new Dictionary<string, string>();
        var found = DocumentExtractor.Rules(Type("commercial.invoice"), text, methods);
        Assert.Equal("INV-2026-044", (string?)found["invoiceNo"]);
        Assert.Equal("Nordic Wear AB", (string?)found["buyer"]);
        Assert.Equal("USD", (string?)found["currency"]);
        var lines = (JsonArray)found["lines"]!;
        Assert.Equal(2, lines.Count);
        Assert.Equal("1200", (string?)lines[0]!["quantity"]);
        Assert.Equal("rules", methods["lines"]);
        // Everything required was found by rules: nothing would be sent to the AI.
        Assert.Empty(DocumentExtractor.Wanted(Type("commercial.invoice"), DocumentRules.Sanitize(Type("commercial.invoice"), found, false)));
    }

    [Fact]
    public void Ai_values_not_found_in_the_document_text_are_rejected()
    {
        var norm = DocumentExtractor.Norm("Invoice No: INV-44 Qty 1,200 pcs dated 14/11/2026 Buyer Nordic");
        Assert.True(DocumentExtractor.Grounded(JsonValue.Create("INV-44"), norm));
        Assert.True(DocumentExtractor.Grounded(JsonValue.Create(1200m), norm));
        Assert.True(DocumentExtractor.Grounded(JsonValue.Create("2026-11-14"), norm));
        Assert.False(DocumentExtractor.Grounded(JsonValue.Create("INV-45"), norm));
        Assert.False(DocumentExtractor.Grounded(JsonValue.Create("Acme Corp"), norm));
        Assert.False(DocumentExtractor.Grounded(new JsonArray(new JsonObject { ["description"] = "Jacket", ["quantity"] = 999 }), norm));
    }

    [Fact]
    public void Classification_by_keywords_prefers_the_matching_type()
    {
        var scores = DocumentExtractor.ScoreTypes(DocumentCatalog.All, "PACKING LIST\nCtn No 1-10, Invoice no INV-1, carton gross weight");
        Assert.Equal("commercial.packing_list", scores[0].Code);
        Assert.Equal("store.grn", DocumentExtractor.ScoreTypes(DocumentCatalog.All, "GOODS RECEIVED NOTE  GRN no 12  Delivery challan 55")[0].Code);
        Assert.Empty(DocumentExtractor.ScoreTypes(DocumentCatalog.All, "lorem ipsum"));
    }

    // ─── Files and rendering ───────────────────────────────────────────────

    private static RenderModel Model() => new("Commercial Invoice", "Main factory · DOC-COM-1",
        new[] { ("Reference", "DOC-COM-1"), ("Status", "APPROVED") }, new[] { ("Buyer", "Nordic Wear AB — বাংলা"), ("Total", "3780") },
        new[] { new RenderTable("Lines", new[] { "Description", "Qty", "Code" }, new IReadOnlyList<string>[] { new[] { "Tee", "1200", "007" } }) },
        new[] { "Note: checked" }, "footer");

    [Fact]
    public void Xlsx_export_is_a_valid_workbook_that_reads_back()
    {
        var bytes = OfficeWriters.Xlsx(Model());
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
        {
            Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
            Assert.NotNull(zip.GetEntry("xl/worksheets/sheet2.xml"));
        }
        Assert.True(DocumentFileReader.TryInspect(bytes, "x.xlsx", 10_000_000, out var info, out _));
        Assert.Equal("xlsx", info!.Format);
        var text = string.Join("\n", XlsxReader.Read(bytes, 100_000).Pages.Select(p => p.Text));
        Assert.Contains("Nordic Wear AB — বাংলা", text);
        Assert.Contains("Tee\t1200\t007", text);
    }

    [Fact]
    public void Docx_and_pdf_exports_are_produced()
    {
        var docx = OfficeWriters.Docx(Model());
        using (var zip = new ZipArchive(new MemoryStream(docx)))
        {
            var xml = new StreamReader(zip.GetEntry("word/document.xml")!.Open()).ReadToEnd();
            Assert.Contains("Nordic Wear AB", xml);
        }
        var pdf = DocumentRender.Pdf(Model());
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public void Pdf_exports_shape_bangla_and_keep_mixed_bangla_english_text()
    {
        // Shaping: ক + ্ + ষ is one conjunct glyph (not three), and the vowel sign ি is drawn before ক.
        var ksha = TextShaper.Shape("ক্ষ", 10, false);
        Assert.Single(ksha.Runs);
        Assert.Equal("NotoSansBengali-Regular.ttf", ksha.Runs[0].Face.Name);
        Assert.True(ksha.Runs[0].Glyphs.Length < 3, $"the conjunct should be formed: glyphs {string.Join(",", ksha.Runs[0].Glyphs)} text {string.Join(",", ksha.Runs[0].Text.Select(ch => ((int)ch).ToString("X4")))}");
        var ki = TextShaper.Shape("কি", 10, false).Runs[0];
        var ka = PdfFonts.Bengali(false).Typeface.GetGlyph(0x0995);
        Assert.Equal(2, ki.Glyphs.Length);
        Assert.Equal(ka, ki.Glyphs[1]);                       // reordered: ি first, then ক
        Assert.True(ki.Points[1].X > ki.Points[0].X);
        // Mixed text is split into Latin and Bengali runs, each in its own font.
        var mixed = TextShaper.Shape("Line 3 লাইন ৩", 10, true);
        Assert.Equal(new[] { "NotoSans-Bold.ttf", "NotoSansBengali-Bold.ttf" }, mixed.Runs.Select(r => r.Face.Name).ToArray());
        // A character neither font has is substituted, never dropped silently.
        Assert.Contains("->", string.Concat(TextShaper.Shape("a → b", 10, false).Runs.Select(r => r.Text)));

        var model = new RenderModel("হাজিরা প্রতিবেদন Attendance Report", "কারখানা · DOC-HR-1",
            new[] { ("Reference", "DOC-HR-1"), ("কারখানা", "Dhaka Unit 2") }, new[] { ("মন্তব্য / Remarks", "শ্রমিকদের উপস্থিতি সন্তোষজনক। Line 3 had 2 late arrivals.") },
            new[] { new RenderTable("Workers", new[] { "Worker ID", "নাম" }, Enumerable.Range(0, 120).Select(i => (IReadOnlyList<string>)new[] { $"W-{i:0000}", "মোঃ রফিকুল ইসলাম কারখানা" }).ToList()) },
            new[] { "Note: সাপ্তাহিক ছুটি" }, "footer");
        var bytes = DocumentRender.Pdf(model);
        using var doc = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.True(doc.NumberOfPages >= 2);                    // the long table flows onto more pages
        var first = doc.GetPage(1);
        Assert.Contains("Attendance Report", first.Text);
        Assert.Contains("W-0000", first.Text);
        Assert.Contains("কারখানা", first.Text);
        Assert.Contains(first.Letters, l => l.FontName.Contains("NotoSansBengali", StringComparison.Ordinal));
        Assert.Contains(first.Letters, l => l.FontName.Contains("NotoSans-", StringComparison.Ordinal));
        Assert.Contains("W-0119", doc.GetPage(doc.NumberOfPages).Text);

        // Regression: the shaper's font data must survive a compacting GC (it once pointed at a movable managed buffer
        // and crashed the process inside hb_shape after the GC moved it).
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        var again = TextShaper.Shape("ক্ষ Attendance", 10, false);
        Assert.Equal(ksha.Runs[0].Glyphs, again.Runs.First(r => r.Face.Name.StartsWith("NotoSansBengali", StringComparison.Ordinal)).Glyphs.Take(ksha.Runs[0].Glyphs.Length).ToArray());
        using var doc2 = UglyToad.PdfPig.PdfDocument.Open(DocumentRender.Pdf(model));
        Assert.Contains("Attendance Report", doc2.GetPage(1).Text);
    }

    [Fact]
    public void Uploads_are_identified_by_their_bytes()
    {
        Assert.True(DocumentFileReader.TryInspect(Encoding.UTF8.GetBytes("%PDF-1.7\n..."), "a.pdf", 1000, out var pdf, out _));
        Assert.Equal("pdf", pdf!.Format);
        Assert.False(DocumentFileReader.TryInspect(Encoding.UTF8.GetBytes("%PDF-1.7\n..."), "a.docx", 1000, out _, out var err));
        Assert.NotNull(err);
        var webp = new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P', 0, 0 };
        Assert.True(DocumentFileReader.TryInspect(webp, "p.webp", 1000, out var img, out _));
        Assert.Equal("image", img!.Format);
        Assert.False(DocumentFileReader.TryInspect(new byte[2000], "big.pdf", 1000, out _, out _));
    }

    // ─── AI client ─────────────────────────────────────────────────────────

    [Fact]
    public void Model_answers_must_be_json_objects()
    {
        Assert.Equal("x", (string?)DocumentAiClient.ParseObject("```json\n{\"a\":\"x\"}\n```", "openai")["a"]);
        Assert.Throws<DocumentExtractionException>(() => DocumentAiClient.ParseObject("Sure! The invoice number is 5.", "openai"));
        Assert.Throws<DocumentExtractionException>(() => DocumentAiClient.ParseObject("[1,2]", "openai"));
        Assert.Equal((10, 3), DocumentAiClient.Tokens("openai", """{"usage":{"prompt_tokens":10,"completion_tokens":3}}"""));
        Assert.Equal((7, 2), DocumentAiClient.Tokens("anthropic", """{"usage":{"input_tokens":7,"output_tokens":2}}"""));
        Assert.Equal((5, 1), DocumentAiClient.Tokens("gemini", """{"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":1}}"""));
    }

    [Fact]
    public async Task Ai_calls_retry_rate_limits_but_not_rejected_keys_and_need_a_configured_provider()
    {
        var handler = new ScriptedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            _ => Json("""{"choices":[{"message":{"content":"{\"fields\":{\"invoiceNo\":\"INV-1\"}}"}}],"usage":{"prompt_tokens":10,"completion_tokens":4}}"""));
        var client = Client(handler, new FakeAiSettings("openai", "gpt-test", "sk-test"));
        var r = await client.CallAsync("document.extract", "sys", "user", null, 100, new AiCallContext(null, null, null, null), CancellationToken.None);
        Assert.Equal("INV-1", (string?)r.Json["fields"]!["invoiceNo"]);
        Assert.Equal(2, handler.Calls);
        Assert.All(handler.Requests, req => Assert.DoesNotContain("sk-test", req.RequestUri!.ToString()));

        var denied = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<DocumentExtractionException>(() =>
            Client(denied, new FakeAiSettings("openai", "gpt-test", "sk-bad")).CallAsync("document.extract", "s", "u", null, 100, new AiCallContext(null, null, null, null), CancellationToken.None));
        Assert.Equal(DocumentExtractionException.Unauthorized, ex.Code);
        Assert.Equal(1, denied.Calls);

        var always = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<DocumentExtractionException>(() =>
            Client(always, new FakeAiSettings("openai", "gpt-test", "sk")).CallAsync("document.extract", "s", "u", null, 100, new AiCallContext(null, null, null, null), CancellationToken.None));
        Assert.Equal(2, always.Calls); // DOCUMENT_AI_MAX_ATTEMPTS=2 in this test

        var none = Client(new ScriptedHandler(), new FakeAiSettings(null, null, null));
        Assert.False(none.Status().Configured);
        var nc = await Assert.ThrowsAsync<DocumentExtractionException>(() => none.CallAsync("x", "s", "u", null, 10, new AiCallContext(null, null, null, null), CancellationToken.None));
        Assert.Equal(DocumentExtractionException.NotConfigured, nc.Code);
    }

    [Fact]
    public async Task Extraction_sends_only_missing_fields_and_drops_ungrounded_ai_values()
    {
        string? sent = null;
        var handler = new ScriptedHandler(req =>
        {
            sent = req.Content!.ReadAsStringAsync().Result;
            return Json("""{"choices":[{"message":{"content":"{\"fields\":{\"buyer\":\"Invented Buyer\",\"invoiceDate\":\"2026-11-14\",\"invoiceNo\":\"OTHER\"}}"}}]}""");
        });
        var ai = Client(handler, new FakeAiSettings("openai", "gpt-test", "sk"));
        var extractor = new DocumentExtractor(ai, Options());
        var text = "Invoice No: INV-9\nCurrency: USD\nPO number: PO-1\nDated 14 Nov 2026\nDescription\tQuantity\tUnit price\nTee\t10\t2";
        var r = await extractor.ExtractAsync(Type("commercial.invoice"), text, null, allowAi: true, new AiCallContext(null, null, null, null), CancellationToken.None);
        Assert.Equal("INV-9", (string?)r.Values["invoiceNo"]);          // rules value kept, AI's "OTHER" ignored (not requested)
        Assert.Equal("2026-11-14", (string?)r.Values["invoiceDate"]);   // grounded (year appears)
        Assert.False(r.Values.ContainsKey("buyer"));                    // invented, not in the text
        Assert.Contains(r.Rejected, x => x.StartsWith("buyer"));
        var prompt = JsonNode.Parse(sent!)!["messages"]![1]!["content"]!.GetValue<string>();
        Assert.DoesNotContain("field \"invoiceNo\"", prompt);   // already found by rules: not asked again
        Assert.Contains("field \"buyer\"", prompt);

        var offline = new DocumentExtractor(Client(new ScriptedHandler(), new FakeAiSettings(null, null, null)), Options());
        var rules = await offline.ExtractAsync(Type("commercial.invoice"), text, null, allowAi: false, new AiCallContext(null, null, null, null), CancellationToken.None);
        Assert.Equal("rules", rules.Method);
        Assert.Equal("INV-9", (string?)rules.Values["invoiceNo"]);
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private static DocumentOptions Options() => DocumentOptions.FromEnvironment(k => k switch { "DOCUMENT_AI_MAX_ATTEMPTS" => "2", _ => null }, Path.GetTempPath());

    private static DocumentAiClient Client(HttpMessageHandler handler, IAiSettingsProvider settings) =>
        new(settings, new SingleClientFactory(handler), Options(), new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DocumentAiClient>.Instance);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>[] _steps;
        public int Calls { get; private set; }
        public List<HttpRequestMessage> Requests { get; } = new();
        public ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) { _steps = steps; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var step = _steps.Length == 0 ? (_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)) : _steps[Math.Min(Calls, _steps.Length - 1)];
            Calls++;
            return Task.FromResult(step(request));
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public SingleClientFactory(HttpMessageHandler h) { _h = h; }
        public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
    }

    private sealed class FakeAiSettings : IAiSettingsProvider
    {
        private readonly AiSettingsSnapshot _snap;
        public FakeAiSettings(string? provider, string? model, string? key)
        {
            var secret = SecretValue.From(key);
            _snap = new AiSettingsSnapshot(new LlmSettings(provider, model, secret, secret is null ? SecretStatus.NotSet : SecretStatus.Set, AiSettingsSource.RuntimeOverride),
                new EmbeddingSettings("local", "hash-md5", 384, null, SecretStatus.NotSet, AiSettingsSource.Default), 1, null, null);
        }
        public ValueTask<AiSettingsSnapshot> GetAsync(CancellationToken ct = default) => ValueTask.FromResult(_snap);
        public AiSettingsSnapshot GetCurrent() => _snap;
        public IDisposable BeginOverride(AiSettingsOverride settingsOverride) => throw new NotSupportedException();
        public StartupAiConfig Startup => StartupAiConfig.Empty;
        public void Invalidate() { }
    }
}
