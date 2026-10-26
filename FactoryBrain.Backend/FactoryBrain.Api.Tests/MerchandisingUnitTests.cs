using System.Text.Json;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Merchandising;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.Merchandising;
using FactoryBrain.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FactoryBrain.Api.Tests;

/// <summary>
/// The merchandising rules without a database: value validation, the
/// extraction contract, evidence checks, the rule-based extractor, document
/// parsing, file sniffing, conflict rules and the attention list.
/// </summary>
public sealed class MerchandisingUnitTests
{
    private static readonly DateOnly Today = new(2026, 10, 27);

    private static FieldNormalization N(string key, string? raw, DateOnly? today = null) => MerchFields.Normalize(key, raw, today);

    // ── Field validation ────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-12-15", "2026-12-15")]
    [InlineData("2026/12/15", "2026-12-15")]
    [InlineData("15-Dec-2026", "2026-12-15")]
    [InlineData("15 December 2026", "2026-12-15")]
    [InlineData("December 15, 2026", "2026-12-15")]
    [InlineData("Dec 15th, 2026", "2026-12-15")]
    [InlineData("15.12.2026", "2026-12-15")]
    [InlineData("25/12/2026", "2026-12-25")]
    [InlineData("12/25/2026", "2026-12-25")]
    [InlineData("05/05/2027", "2027-05-05")]
    public void Dates_in_unambiguous_formats_are_normalised(string raw, string expected)
    {
        var n = N(MerchFields.DeliveryDate, raw);
        Assert.Equal(expected, n.Normalized);
        Assert.Empty(n.Issues);
    }

    [Theory]
    [InlineData("03/04/2027", MerchFields.IssueAmbiguousDate)]
    [InlineData("03-04-2027", MerchFields.IssueAmbiguousDate)]
    [InlineData("03/04/27", MerchFields.IssueAmbiguousDate)]
    [InlineData("31/02/2027", MerchFields.IssueInvalidDate)]
    [InlineData("next month", MerchFields.IssueInvalidDate)]
    [InlineData("1890-01-01", MerchFields.IssueInvalidDate)]
    public void Ambiguous_or_invalid_dates_are_never_guessed(string raw, string issue)
    {
        var n = N(MerchFields.DeliveryDate, raw);
        Assert.Null(n.Normalized);
        Assert.Equal(new[] { issue }, n.Issues);
        Assert.True(n.HasBlockingIssue);
    }

    [Fact]
    public void A_past_delivery_date_is_kept_with_a_warning()
    {
        var n = N(MerchFields.DeliveryDate, "2026-10-01", Today);
        Assert.Equal("2026-10-01", n.Normalized);
        Assert.Equal(new[] { MerchFields.IssueDateInPast }, n.Issues);
        Assert.False(n.HasBlockingIssue);
    }

    [Theory]
    [InlineData("12,000 pcs", "12000")]
    [InlineData("12 000", "12000")]
    [InlineData("12000.00", "12000")]
    [InlineData("1,200 Pieces", "1200")]
    [InlineData("500 dz", "500")]
    public void Quantities_are_whole_numbers(string raw, string expected) => Assert.Equal(expected, N(MerchFields.Quantity, raw).Normalized);

    [Theory]
    [InlineData("12.000", MerchFields.IssueAmbiguousQuantity)]
    [InlineData("12.5", MerchFields.IssueInvalidQuantity)]
    [InlineData("12000 boxes", MerchFields.IssueInvalidQuantity)]
    [InlineData("approx. 12k", MerchFields.IssueInvalidQuantity)]
    [InlineData("0", MerchFields.IssueQuantityRange)]
    [InlineData("20,000,000", MerchFields.IssueQuantityRange)]
    [InlineData("", MerchFields.IssueEmpty)]
    public void Invalid_quantities_are_flagged(string raw, string issue) => Assert.Equal(new[] { issue }, N(MerchFields.Quantity, raw).Issues);

    [Theory]
    [InlineData("Pieces", "pcs")]
    [InlineData("PCS.", "pcs")]
    [InlineData("dz", "dozen")]
    [InlineData("Sets", "sets")]
    [InlineData("prs", "pairs")]
    public void Units_map_to_the_canonical_set(string raw, string expected) => Assert.Equal(expected, N(MerchFields.Unit, raw).Normalized);

    [Fact]
    public void An_unknown_unit_is_flagged() => Assert.Equal(new[] { MerchFields.IssueUnknownUnit }, N(MerchFields.Unit, "bags").Issues);

    [Theory]
    [InlineData("usd", "USD", null)]
    [InlineData("US$", "USD", null)]
    [InlineData("€", "EUR", null)]
    [InlineData("Tk", "BDT", null)]
    [InlineData("$", null, MerchFields.IssueAmbiguousCurrency)]
    [InlineData("¥", null, MerchFields.IssueAmbiguousCurrency)]
    [InlineData("XYZ", null, MerchFields.IssueInvalidCurrency)]
    public void Currencies_are_iso_codes_and_ambiguous_symbols_are_not_guessed(string raw, string? expected, string? issue)
    {
        var n = N(MerchFields.Currency, raw);
        Assert.Equal(expected, n.Normalized);
        Assert.Equal(issue is null ? Array.Empty<string>() : new[] { issue }, n.Issues);
    }

    [Theory]
    [InlineData("USD 4.25", "4.25")]
    [InlineData("$4.25", "4.25")]
    [InlineData("1,234.50", "1234.5")]
    [InlineData("4,25", "4.25")]
    [InlineData("4.2500", "4.25")]
    public void Prices_are_positive_decimals(string raw, string expected) => Assert.Equal(expected, N(MerchFields.UnitPrice, raw).Normalized);

    [Theory]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("4.12345")]
    [InlineData("free")]
    public void Invalid_prices_are_flagged(string raw) => Assert.Equal(new[] { MerchFields.IssueInvalidPrice }, N(MerchFields.UnitPrice, raw).Issues);

    [Fact]
    public void Text_fields_have_length_limits()
    {
        Assert.Equal(new[] { MerchFields.IssueTooLong }, N(MerchFields.OrderNumber, new string('9', 65)).Issues);
        Assert.Equal("PO 4500 1", N(MerchFields.OrderNumber, "  PO   4500\t1 ").Normalized);
    }

    [Fact]
    public void Breakdowns_are_validated_and_totalled()
    {
        var ok = N(MerchFields.Breakdown, """[{"color":"Navy","size":"M","quantity":120},{"colour":"Navy","size":"L","quantity":"1,080"}]""");
        Assert.Empty(ok.Issues);
        var rows = MerchFields.ParseBreakdown(ok.Normalized)!;
        Assert.Equal(1200, MerchFields.BreakdownTotal(rows));
        Assert.Equal("L", rows[1].Size);

        Assert.Equal(new[] { MerchFields.IssueBreakdownDuplicate },
            N(MerchFields.Breakdown, """[{"color":"Navy","size":"M","quantity":1},{"color":" navy ","size":"m","quantity":2}]""").Issues);
        Assert.Equal(new[] { MerchFields.IssueInvalidBreakdown }, N(MerchFields.Breakdown, """[{"color":"Navy","quantity":1}]""").Issues);
        Assert.Equal(new[] { MerchFields.IssueInvalidBreakdown }, N(MerchFields.Breakdown, """[{"color":"Navy","size":"M","quantity":-4}]""").Issues);
        Assert.Equal(new[] { MerchFields.IssueInvalidBreakdown }, N(MerchFields.Breakdown, "not json").Issues);
    }

    // ── Extraction contract ─────────────────────────────────────────────────

    [Fact]
    public void Model_output_is_schema_checked()
    {
        var content = """
            ```json
            {"fields":[
              {"key":"quantity","value":"12,000","confidence":0.92,"page":1,"evidence":"Quantity: 12,000"},
              {"key":"QUANTITY","value":"11,000","confidence":0.5},
              {"key":"payment_terms","value":"LC 90","confidence":0.9},
              {"key":"style","value":4711,"confidence":1.4,"page":-2},
              {"key":"buyer","value":null},
              {"key":"currency","value":{"code":"USD"}}
            ],
             "breakdown":{"rows":[{"color":"Navy","size":"M","quantity":12000}],"confidence":0.8,"page":2,"evidence":"Navy M 12000"}}
            ```
            """;
        var fields = ExtractionOutputParser.Parse(content);
        Assert.Equal(new[] { "quantity", "quantity", "style", MerchFields.Breakdown }, fields.Select(f => f.FieldKey));
        var style = fields.Single(f => f.FieldKey == "style");
        Assert.Equal("4711", style.Value);
        Assert.Null(style.Confidence);   // outside 0..1: discarded, not clamped
        Assert.Null(style.Page);
        Assert.Equal(0.8, fields.Last().Confidence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Here are the fields: quantity 12000")]
    [InlineData("""{"result":[]}""")]
    [InlineData("""{"fields":"none"}""")]
    [InlineData("""{"fields":["quantity"]}""")]
    public void Output_that_is_not_the_contract_is_refused(string content)
    {
        var ex = Assert.Throws<DocumentExtractionException>(() => ExtractionOutputParser.Parse(content));
        Assert.Equal(DocumentExtractionException.InvalidOutput, ex.Code);
    }

    [Fact]
    public void Prompt_carries_page_markers_and_is_cut_to_the_limit()
    {
        var pages = new[] { new PageText(1, "PO Number: 1\n" + new string('x', 3000)), new PageText(2, "Quantity: 5") };
        var prompt = ExtractionOutputParser.BuildPrompt(new ExtractionRequest("purchase_order", pages), 1500, out var truncated);
        Assert.True(truncated);
        Assert.Contains("=== Page 1 ===", prompt);
        Assert.DoesNotContain("=== Page 2 ===", prompt);
        Assert.Contains("order_number", prompt);
    }

    // ── Evidence ────────────────────────────────────────────────────────────

    private static readonly PageText[] Pages =
    {
        new(1, "PURCHASE ORDER\nPO Number:   4500123\nBuyer: Nordic Wear AB"),
        new(2, "Total Quantity: 12,000 pcs\nDelivery Date: 15-Dec-2026"),
    };

    [Fact]
    public void Evidence_must_occur_in_the_document()
    {
        Assert.Equal((1, "PO Number: 4500123"), MerchEvidence.Locate("po number: 4500123", 1, Pages));
        // Wrong page cited: found on the page it is really on.
        Assert.Equal(2, MerchEvidence.Locate("Total Quantity: 12,000 pcs", 1, Pages)!.Value.Page);
        Assert.Null(MerchEvidence.Locate("Quantity: 13,000 pcs", 2, Pages));
        Assert.Null(MerchEvidence.Locate("x", 1, Pages));
    }

    [Fact]
    public void Candidates_become_validated_fields_with_checked_evidence()
    {
        var candidates = new ExtractedCandidate[]
        {
            new("quantity", "11,000", 0.5, 2, "Total Quantity: 11,000 pcs"),        // lower confidence duplicate: dropped
            new("quantity", "12,000 pcs", 0.9, 2, "Total Quantity: 12,000 pcs"),
            new("order_number", "4500123", 0.95, 1, "PO Number: 4500123"),
            new("unit_price", "9.99", 0.99, 1, "Unit Price: 9.99"),                  // quote not in the document
            new("buyer", "Nordic Wear AB", null, null, null),                         // no quote
            new("style", "ST-1", 0.2, 1, "Buyer: Nordic Wear AB"),                    // low confidence
            new("delivery_date", "03/04/2027", 0.9, 2, "Delivery Date: 15-Dec-2026"),
            new(MerchFields.Breakdown, """[{"color":"Navy","size":"M","quantity":11000}]""", 0.8, 2, "Total Quantity: 12,000 pcs"),
            new("payment_terms", "LC", 0.9, 1, "x"),
        };
        var drafts = MerchExtractionNormalizer.Build(candidates, Pages, MerchFieldSource.Model, 0.6, Today).ToDictionary(d => d.Key);
        Assert.Equal(7, drafts.Count);
        Assert.Equal("12000", drafts["quantity"].NormalizedValue);
        Assert.Equal(MerchEvidence.Verified, drafts["quantity"].EvidenceStatus);
        Assert.Equal(2, drafts["quantity"].Page);
        Assert.Empty(drafts["quantity"].Issues);

        var price = drafts["unit_price"];
        Assert.Equal(MerchEvidence.NotFound, price.EvidenceStatus);
        Assert.Null(price.Snippet);                                // never stored as evidence
        Assert.Contains(MerchFields.IssueEvidenceNotFound, price.Issues);
        Assert.Equal("9.99", price.NormalizedValue);               // the value is kept for the reviewer to judge

        Assert.Contains(MerchFields.IssueNoEvidence, drafts["buyer"].Issues);
        Assert.Contains(MerchFields.IssueLowConfidence, drafts["style"].Issues);
        Assert.Contains(MerchFields.IssueAmbiguousDate, drafts["delivery_date"].Issues);
        Assert.Null(drafts["delivery_date"].NormalizedValue);
        Assert.Contains(MerchFields.IssueBreakdownTotal, drafts[MerchFields.Breakdown].Issues);
    }

    // ── Rule-based extractor ────────────────────────────────────────────────

    private const string PoText = """
        PURCHASE ORDER
        PO Date: 2026-10-20
        PO No: 4500123
        Buyer: Nordic Wear AB
        Style No: ST-1001
        Description: Men's knit polo shirt
        Total Qty (pcs): 1,200
        Unit Price: USD 4.25
        Currency: USD
        Ex-Factory Date: 15-Dec-2026
        Ship To: Gothenburg, Sweden

        Colour      S      M      L      Total
        Navy        100    200    300    600
        White       100    200    300    600
        Total       200    400    600    1200
        """;

    [Fact]
    public void Rule_based_extractor_reads_labelled_values_and_size_matrices_with_their_lines()
    {
        var found = RuleBasedFieldExtractor.Extract(new[] { new PageText(1, PoText) }).ToDictionary(c => c.FieldKey);
        Assert.Equal("4500123", found["order_number"].Value);     // "PO Date" is not the PO number
        Assert.Equal("PO No: 4500123", found["order_number"].Evidence);
        Assert.Equal("Nordic Wear AB", found["buyer"].Value);
        Assert.Equal("ST-1001", found["style"].Value);
        Assert.Equal("Men's knit polo shirt", found["product"].Value);
        Assert.Equal("1,200", found["quantity"].Value);
        Assert.Equal("pcs", found["unit"].Value);                  // from "(pcs)" on the same line
        Assert.Equal("USD 4.25", found["unit_price"].Value);
        Assert.Equal("USD", found["currency"].Value);
        Assert.Equal("15-Dec-2026", found["delivery_date"].Value);
        Assert.Equal("Gothenburg, Sweden", found["destination"].Value);
        Assert.All(found.Values, c => Assert.Null(c.Confidence));
        var rows = MerchFields.ParseBreakdown(found[MerchFields.Breakdown].Value)!;
        Assert.Equal(6, rows.Count);                               // the Total row and column are not sizes
        Assert.Equal(1200, MerchFields.BreakdownTotal(rows));

        // Every quote is found back in the page.
        var drafts = MerchExtractionNormalizer.Build(found.Values.ToList(), new[] { new PageText(1, PoText) }, MerchFieldSource.Rules, 0.6, Today);
        Assert.All(drafts, d => Assert.Equal(MerchEvidence.Verified, d.EvidenceStatus));
        Assert.All(drafts, d => Assert.Empty(d.Issues));
    }

    [Fact]
    public void Rule_based_extractor_reads_long_form_tables_and_csv_rows()
    {
        var csv = DocumentTextParser.ParseCsv("Field,Value\nPO Number,4500999\nQuantity,\"2,400\"\n\nColor,Size,Qty\nRed,S,1200\nRed,M,1200\n");
        var found = RuleBasedFieldExtractor.Extract(new[] { new PageText(1, csv) }).ToDictionary(c => c.FieldKey);
        Assert.Equal("4500999", found["order_number"].Value);
        Assert.Equal("2,400", found["quantity"].Value);
        var rows = MerchFields.ParseBreakdown(found[MerchFields.Breakdown].Value)!;
        Assert.Equal(new[] { ("Red", "S", 1200), ("Red", "M", 1200) }, rows.Select(r => (r.Color, r.Size, r.Quantity)));
    }

    [Fact]
    public void Rule_based_extractor_finds_nothing_in_unlabelled_prose()
    {
        var found = RuleBasedFieldExtractor.Extract(new[] { new PageText(1, "Please ship twelve thousand navy polos before Christmas.") });
        Assert.Empty(found);
    }

    // ── Conflict rules ──────────────────────────────────────────────────────

    private static ConflictSource Doc(string key, string value, int version = 1) =>
        new(key, value, "document", Guid.NewGuid(), $"Doc {version}", "purchase_order", Guid.Parse($"00000000-0000-0000-0000-00000000000{version}"), version);

    [Fact]
    public void Style_and_order_numbers_compare_without_case_or_punctuation()
    {
        Assert.Empty(MerchConflictDetector.Detect(new[] { Doc("style", "ST-1001", 1), Doc("style", "st 1001", 2) }));
        Assert.Single(MerchConflictDetector.Detect(new[] { Doc("style", "ST-1001", 1), Doc("style", "ST-1002", 2) }));
    }

    [Fact]
    public void Quantities_dates_and_breakdowns_conflict_on_different_values_only()
    {
        Assert.Single(MerchConflictDetector.Detect(new[] { Doc("quantity", "12000", 1), Doc("quantity", "11800", 2) }));
        Assert.Empty(MerchConflictDetector.Detect(new[] { Doc("unit_price", "4.25", 1), Doc("unit_price", "4.2500", 2) }));
        var a = """[{"color":"Navy","size":"M","quantity":1},{"color":"Navy","size":"L","quantity":2}]""";
        var b = """[{"color":"NAVY","size":"L","quantity":2},{"color":"navy","size":"m","quantity":1}]""";
        var c = """[{"color":"Navy","size":"M","quantity":2},{"color":"Navy","size":"L","quantity":1}]""";
        Assert.Empty(MerchConflictDetector.Detect(new[] { Doc(MerchFields.Breakdown, a, 1), Doc(MerchFields.Breakdown, b, 2) }));
        Assert.Single(MerchConflictDetector.Detect(new[] { Doc(MerchFields.Breakdown, a, 1), Doc(MerchFields.Breakdown, c, 2) }));
        // Free text is not compared.
        Assert.Empty(MerchConflictDetector.Detect(new[] { Doc("buyer", "Nordic Wear", 1), Doc("buyer", "Nordic Wear AB", 2) }));
    }

    [Fact]
    public void Fingerprint_identifies_the_exact_sources_and_values()
    {
        var one = MerchConflictDetector.Detect(new[] { Doc("quantity", "12000", 1), Doc("quantity", "11800", 2) }).Single();
        var same = MerchConflictDetector.Detect(new[] { Doc("quantity", "11800", 2), Doc("quantity", "12000", 1) }).Single();
        var withOrder = MerchConflictDetector.Detect(new[] { Doc("quantity", "12000", 1), Doc("quantity", "11800", 2), new ConflictSource("quantity", "12000", "order") }).Single();
        Assert.Equal(one.Fingerprint, same.Fingerprint);
        Assert.NotEqual(one.Fingerprint, withOrder.Fingerprint);
        Assert.Equal("order", withOrder.Values.Last().Kind);
    }

    // ── Attention list and summary ──────────────────────────────────────────

    [Fact]
    public void Attention_items_are_prioritised_by_fixed_rules()
    {
        var milestone = Guid.NewGuid();
        var result = MerchAttention.Build(new AttentionInput(
            new AttentionOrder("Nordic Wear AB", "4500123", null, 1200, "pcs", Today.AddDays(20), MerchOrderStatus.Active,
                new[] { new BreakdownRow("Navy", "M", 1000) }, new[] { "purchase_order", "tech_pack" }),
            new[]
            {
                new AttentionMilestone(milestone, "Lab dip approval", Today.AddDays(-3), MerchMilestoneStatus.Pending),
                new AttentionMilestone(Guid.NewGuid(), "PP sample", Today.AddDays(5), MerchMilestoneStatus.Pending),
                new AttentionMilestone(Guid.NewGuid(), "Fabric booking", Today.AddDays(-10), MerchMilestoneStatus.Done),
            },
            new[] { new AttentionDocument(Guid.NewGuid(), "Purchase order", "purchase_order", Guid.NewGuid(), 1, MerchVersionStatus.NeedsReview, null, null, Array.Empty<string>()) },
            new[] { new AttentionConflict(Guid.NewGuid(), "quantity", 2), new AttentionConflict(Guid.NewGuid(), "currency", 2) },
            new[] { new AttentionStaleField("unit_price", "Purchase order", 1) },
            null, null, Array.Empty<string>(), Today));

        var kinds = result.Items.Select(i => (i.Kind, i.Priority)).ToList();
        Assert.Equal(("milestone_overdue", "high"), kinds[0]);       // dated high items first
        Assert.Contains(("conflict", "high"), kinds);                // quantity
        Assert.Contains(("conflict", "medium"), kinds);              // currency
        Assert.Contains(("missing_document", "high"), kinds);        // tech pack, delivery within 30 days
        Assert.Contains(("breakdown_mismatch", "high"), kinds);
        Assert.Contains(("needs_review", "medium"), kinds);
        Assert.Contains(("missing_field", "medium"), kinds);         // style
        Assert.Contains(("milestone_upcoming", "low"), kinds);
        Assert.Equal(("stale_source", "low"), kinds.Last(k => k.Priority == "low" && k.Kind == "stale_source"));
        Assert.True(kinds.FindLastIndex(k => k.Priority == "high") < kinds.FindIndex(k => k.Priority == "medium"));
        Assert.Equal(milestone, result.Items[0].MilestoneId);

        Assert.Equal("missing", result.RequiredDocuments.Single(r => r.Type == "tech_pack").State);
        Assert.Equal("needs_review", result.RequiredDocuments.Single(r => r.Type == "purchase_order").State);
        Assert.Equal("Nordic Wear AB PO 4500123: 1,200 pcs, delivery 2026-11-16 (in 20 days).", result.Summary.Headline);
        Assert.Contains("Milestones: 1 of 3 done; next: Lab dip approval on 2026-10-24; 1 overdue.", result.Summary.Lines);
        Assert.Contains(result.Summary.Lines, l => l.StartsWith("Breakdown: 1 row(s) totalling 1,000 (quantity is 1,200)"));
    }

    // ── Files ───────────────────────────────────────────────────────────────

    [Fact]
    public void Files_are_identified_by_content_and_the_extension_must_agree()
    {
        InspectedFile? f;
        string? e;
        Assert.True(MerchFileInspector.TryInspect(TestDocuments.Pdf(new[] { "x" }), "po.pdf", 1 << 20, out f, out _));
        Assert.Equal("pdf", f!.Format);
        Assert.True(MerchFileInspector.TryInspect(TestDocuments.Docx(new[] { "x" }), "spec.docx", 1 << 20, out f, out _));
        Assert.Equal("docx", f!.Format);
        Assert.True(MerchFileInspector.TryInspect(TestDocuments.Text("a,b"), "sizes.csv", 1 << 20, out f, out _));
        Assert.Equal("csv", f!.Format);
        Assert.True(MerchFileInspector.TryInspect(TestImages.Png(8, 8), "scan.png", 1 << 20, out f, out _));
        Assert.Equal("image", f!.Format);
        Assert.True(MerchFileInspector.TryInspect(new byte[] { 0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0 }, "note.txt", 1 << 20, out f, out _));

        Assert.False(MerchFileInspector.TryInspect(TestDocuments.Xlsx(), "sizes.xlsx", 1 << 20, out _, out e));
        Assert.Equal(MerchFileInspector.ErrSpreadsheet, e);
        Assert.False(MerchFileInspector.TryInspect(TestDocuments.Xlsx(), "sizes.zip", 1 << 20, out _, out e));
        Assert.Equal(MerchFileInspector.ErrUnsupported, e);
        Assert.False(MerchFileInspector.TryInspect(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 1, 2 }, "old.doc", 1 << 20, out _, out e));
        Assert.Equal(MerchFileInspector.ErrLegacyOffice, e);
        Assert.False(MerchFileInspector.TryInspect(TestDocuments.Text("hello"), "po.pdf", 1 << 20, out _, out e));
        Assert.Equal(MerchFileInspector.ErrMismatch, e);
        Assert.False(MerchFileInspector.TryInspect(TestDocuments.Pdf(new[] { "x" }), "po.txt", 1 << 20, out _, out e));
        Assert.Equal(MerchFileInspector.ErrMismatch, e);
        Assert.False(MerchFileInspector.TryInspect(new byte[] { 1, 2, 3, 0, 4 }, "data.txt", 1 << 20, out _, out e));
        Assert.Equal(MerchFileInspector.ErrNotText, e);
        Assert.False(MerchFileInspector.TryInspect(new byte[] { 0xC3, 0x28 }, "bad.txt", 1 << 20, out _, out e));
        Assert.Equal(MerchFileInspector.ErrNotText, e);
        Assert.False(MerchFileInspector.TryInspect(Array.Empty<byte>(), "po.pdf", 1 << 20, out _, out e));
        Assert.Equal(MerchFileInspector.ErrEmpty, e);
        Assert.False(MerchFileInspector.TryInspect(new byte[2048], "big.txt", 1024, out _, out e));
        Assert.Equal(MerchFileInspector.ErrTooLarge, e);
        Assert.False(MerchFileInspector.TryInspect(TestDocuments.Text("x"), "run.exe", 1 << 20, out _, out e));
        Assert.Equal(MerchFileInspector.ErrUnsupported, e);
    }

    [Fact]
    public void Pdf_text_is_read_per_page()
    {
        var pdf = TestDocuments.Pdf(new[] { "PURCHASE ORDER", "PO Number: 4500123" }, new[] { "Quantity: 12,000 pcs" });
        var parsed = DocumentTextParser.Parse(pdf, "pdf", 10, 100_000);
        Assert.Equal(2, parsed.PageCount);
        Assert.Contains("PO Number: 4500123", parsed.Pages[0].Text);
        Assert.Contains("Quantity: 12,000 pcs", parsed.Pages[1].Text);
        Assert.Equal(2, parsed.Pages[1].PageNumber);
    }

    [Theory]
    [InlineData("scanned", DocumentParseException.OcrRequired)]
    [InlineData("garbage", DocumentParseException.Malformed)]
    [InlineData("pages", DocumentParseException.TooManyPages)]
    [InlineData("image", DocumentParseException.OcrRequired)]
    [InlineData("empty-text", DocumentParseException.NoText)]
    public void Unreadable_documents_report_why(string kind, string code)
    {
        var (bytes, format) = kind switch
        {
            "scanned" => (TestDocuments.ScannedPdf(), "pdf"),
            "garbage" => (TestDocuments.Text("%PDF-1.7\n1 0 obj << /Type /Catalog /Pages 9 0 R >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF"), "pdf"),
            "pages" => (TestDocuments.Pdf(new[] { "a page" }, new[] { "b page" }, new[] { "c page" }), "pdf"),
            "image" => (TestImages.Png(8, 8), "image"),
            _ => (TestDocuments.Text("   \n\t "), "txt"),
        };
        var ex = Assert.Throws<DocumentParseException>(() => DocumentTextParser.Parse(bytes, format, 2, 100_000));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void Docx_paragraphs_and_table_rows_are_read()
    {
        var docx = TestDocuments.Docx(new[] { "TECH PACK", "Style No: ST-1001" }, new[] { new[] { "Colour", "Size", "Qty" }, new[] { "Navy", "M", "600" } });
        var text = DocumentTextParser.Parse(docx, "docx", 10, 100_000).Pages.Single().Text;
        Assert.Contains("Style No: ST-1001\n", text);
        Assert.Contains("Colour\tSize\tQty\nNavy\tM\t600\n", text);
        var found = RuleBasedFieldExtractor.Extract(new[] { new PageText(1, text) });
        Assert.Contains(found, c => c.FieldKey == MerchFields.Breakdown);
    }

    [Fact]
    public void Csv_quotes_and_delimiters_are_honoured()
    {
        Assert.Equal("Quantity\t12,000\nNote\tsay \"hi\" there\n", DocumentTextParser.ParseCsv("Quantity,\"12,000\"\r\nNote,\"say \"\"hi\"\"\nthere\"\r\n"));
        Assert.Equal("a\tb\n", DocumentTextParser.ParseCsv("a;b\n"));
    }

    [Fact]
    public async Task The_document_store_refuses_keys_it_did_not_generate()
    {
        var root = Path.Combine(Path.GetTempPath(), "fb-merch-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SafeFileStore(root, new[] { "pdf", "txt" }, NullLogger.Instance, "test document");
            var key = await store.SaveAsync(new byte[] { 1, 2, 3 }, "pdf", CancellationToken.None);
            Assert.True(store.IsValidKey(key));
            await using (var s = await store.OpenReadAsync(key, CancellationToken.None)) Assert.NotNull(s);
            foreach (var bad in new[] { "../../etc/passwd", "2026/10/../../x.pdf", "C:\\Windows\\win.ini", key.Replace(".pdf", ".exe"), key.ToUpperInvariant() })
            {
                Assert.False(store.IsValidKey(bad), bad);
                Assert.Null(await store.OpenReadAsync(bad, CancellationToken.None));
            }
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new byte[] { 1 }, "exe", CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // ── Configuration ───────────────────────────────────────────────────────

    [Fact]
    public async Task Options_default_to_rule_based_extraction_and_reject_unknown_modes()
    {
        var defaults = MerchOptions.FromEnvironment(_ => null, "/srv/app");
        Assert.Equal(MerchOptions.ModeRules, defaults.Mode);
        Assert.Equal(15L * 1024 * 1024, defaults.MaxFileBytes);
        Assert.Null(defaults.ConfigurationError);

        var env = new Dictionary<string, string?> { ["MERCH_EXTRACTION_PROVIDER"] = "magic", ["MERCH_DOCUMENT_MAX_MB"] = "500", ["MERCH_REVIEW_CONFIDENCE"] = "2" };
        var o = MerchOptions.FromEnvironment(k => env.GetValueOrDefault(k), "/srv/app");
        Assert.Equal(MerchOptions.ModeNone, o.Mode);
        Assert.NotNull(o.ConfigurationError);
        Assert.Equal(MerchOptions.MaxFileBytesCeiling, o.MaxFileBytes);
        Assert.Equal(0.6, o.ReviewConfidence);

        var none = new ConfiguredExtractionProvider(o, new ServiceCollection().BuildServiceProvider());
        Assert.False(none.Describe().Configured);
        var ex = await Assert.ThrowsAsync<DocumentExtractionException>(() => none.ExtractAsync(new ExtractionRequest("other", Array.Empty<PageText>()), CancellationToken.None));
        Assert.Equal(DocumentExtractionException.NotConfigured, ex.Code);
        Assert.True(new RulesExtractionProvider().Describe() is { Configured: true, ReportsConfidence: false });
    }

    [Fact]
    public void Breakdown_json_round_trips()
    {
        var rows = new[] { new BreakdownRow("Navy", "M", 3) };
        var json = MerchFields.SerializeBreakdown(rows);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Navy", doc.RootElement[0].GetProperty("color").GetString());
        Assert.Equal(rows, MerchFields.ParseBreakdown(json));
    }
}
