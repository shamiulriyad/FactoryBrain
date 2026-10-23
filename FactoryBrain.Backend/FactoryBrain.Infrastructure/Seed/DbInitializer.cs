using FactoryBrain.Domain.Entities;
using FactoryBrain.Domain.Enums;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Infrastructure.Rag;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FactoryBrain.Infrastructure.Persistence;

/// <summary>
/// Idempotent data seeder. Mirrors every record under
/// <c>src/data/*.ts</c> so the frontend sees the exact same widgets, counts
/// and ordering when it talks to ASP.NET Core.
/// </summary>
public static class DbInitializer
{
    /// <summary>
    /// Convenience entry point invoked from <c>Program.cs</c> on startup.
    /// Pulls a scoped <see cref="FactoryBrainDbContext"/> + <see cref="IRagService"/>
    /// out of the root provider and runs the full seed pass.
    /// </summary>
    public static async Task Initialize(IServiceProvider sp, CancellationToken ct = default)
        => await SeedAsync(sp, ct);

    public static async Task SeedAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryBrainDbContext>();
        var rag = scope.ServiceProvider.GetRequiredService<IRagService>();
        var embedder = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
        var admin = scope.ServiceProvider.GetRequiredService<EmbeddingColumnAdmin>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");

        await SeedAgentsAsync(db, ct);
        await SeedPoliciesAsync(db, ct);
        await SeedManualsAsync(db, rag, embedder, admin, ct);
        await SeedRmgDemoCorpusAsync(db, rag, admin, ct);
        await SeedLineBoardAsync(db, ct);
        await SeedSensorReadingsAsync(db, ct);
        await SeedQcDefectsAsync(db, ct);
        await SeedFloorAlertsAsync(db, ct);
        await SeedOrdersAndInventoryAsync(db, ct);
        await SeedAdminUserAsync(db, hasher, log, ct);
        await SeedViewerUserAsync(db, hasher, log, ct);
    }

    // --- agents -----------------------------------------------------------
    private static async Task SeedAgentsAsync(FactoryBrainDbContext db, CancellationToken ct)
    {
        if (await db.Agents.AnyAsync(ct)) return;

        db.Agents.AddRange(
            new AgentDefinition
            {
                Id = "line-throughput-agent",
                Name = "Line Efficiency Agent",
                NameBn = "লাইন দক্ষতা এজেন্ট",
                Purpose =
                    "Reads line and order data, computes efficiency against target, and names the bottleneck operation dragging the line down.",
                PurposeBn =
                    "লাইন ও অর্ডার ডেটা পড়ে লক্ষ্যমাত্রার বিপরীতে দক্ষতা গণনা করে এবং লাইনকে পিছিয়ে রাখা বটলনেক অপারেশন চিহ্নিত করে।",
                Risk = RiskTier.Low,
                Execution = "auto",
                Model = "Mid-tier reasoning (GPT-4.1-mini / Claude Sonnet small)",
                ContextSlices = new() { "lines", "orders", "targets" },
                Status = "ready",
                TasksToday = 14,
                RecentCount = 187,
                Glyph = "line-throughput"
            },
            new AgentDefinition
            {
                Id = "maintenance-agent",
                Name = "Maintenance & Uptime Agent",
                NameBn = "মেইনটেন্যান্স ও আপটাইম এজেন্ট",
                Purpose =
                    "Reads machine sensor data (vibration, temperature, duty cycle) and flags machines trending toward failure before they go down.",
                PurposeBn =
                    "মেশিনের সেন্সর ডেটা (কম্পন, তাপমাত্রা, ডিউটি সাইকেল) পড়ে ব্যর্থতার দিকে এগোচ্ছে এমন মেশিন আগেই চিহ্নিত করে।",
                Risk = RiskTier.Medium,
                Execution = "auto_suggest_with_threshold",
                Model = "Mid-tier reasoning + structured threshold checks (GPT-4.1-mini)",
                ContextSlices = new() { "machines", "lines" },
                Status = "ready",
                TasksToday = 8,
                RecentCount = 92,
                Glyph = "maintenance"
            },
            new AgentDefinition
            {
                Id = "manager-agent",
                Name = "Manager Orchestrator Agent",
                NameBn = "ম্যানেজার অর্কেস্ট্রেটর এজেন্ট",
                Purpose =
                    "Routes free-text factory-floor questions to the right sub-agent and generates the daily brief (overnight efficiency, downtime, today's shipment risk) on demand.",
                PurposeBn =
                    "মুক্ত-পাঠ্য কারখানা-তল প্রশ্নগুলো সঠিক সাব-এজেন্টে রাউট করে এবং চাহিদা অনুযায়ী দৈনিক ব্রিফ (রাতের দক্ষতা, ডাউনটাইম, আজকের শিপমেন্ট ঝুঁকি) তৈরি করে।",
                Risk = RiskTier.Low,
                Execution = "auto",
                Model = "Frontier-tier routing + summarisation (Claude Sonnet / GPT-4.1)",
                ContextSlices = new() { "*" },
                Status = "ready",
                TasksToday = 22,
                RecentCount = 248,
                Glyph = "orchestrator"
            });

        await db.SaveChangesAsync(ct);
    }

    // --- policies ---------------------------------------------------------
    private static async Task SeedPoliciesAsync(FactoryBrainDbContext db, CancellationToken ct)
    {
        if (await db.Policies.AnyAsync(ct)) return;
        db.Policies.AddRange(
            new PolicyDocument
            {
                Id = "policy:return-1",
                Title = "Return Policy (BD)",
                TitleBn = "পণ্য ফেরত নীতি (বাংলাদেশ)",
                Type = "return",
                EffectiveDate = new DateTime(2025, 11, 1),
                Body = "Customers may return unworn, unused items within 7 days of delivery for a full refund. " +
                       "Returned items must include original packaging. Perishable grocery items are not returnable " +
                       "unless damaged in transit. Refunds are issued to the original payment method within 5 business " +
                       "days of receiving the returned item. Return shipping is paid by the customer except in cases of our error.",
                BodyBn = "গ্রাহকরা ডেলিভারির ৭ দিনের মধ্যে অব্যবহৃত পণ্য ফেরত দিয়ে সম্পূর্ণ রিফান্ড পেতে পারেন। " +
                         "ফেরত পণ্যসামগ্রীর সাথে মূল প্যাকেজিং থাকতে হবে।"
            },
            new PolicyDocument
            {
                Id = "policy:supplier-agreement-1",
                Title = "Supplier Agreement — Narayanganj Textiles & Sylhet Tea & Beauty",
                TitleBn = "সরবরাহকারী চুক্তি — নারায়ণগঞ্জ টেক্সটাইল ও সিলেট চা",
                Type = "supplier-agreement",
                EffectiveDate = new DateTime(2025, 8, 15),
                Body = "Standard supplier agreement: 30-day payment terms, 2% early-payment discount within 7 days. " +
                       "Quality acceptance window: 5 days from delivery. Rejection requires photographic evidence.",
                BodyBn = "স্ট্যান্ডার্ড সরবরাহকারী চুক্তি: ৩০ দিনের পেমেন্ট শর্ত, ৭ দিনের মধ্যে ২% আগাম পেমেন্ট ছাড়।"
            });
        await db.SaveChangesAsync(ct);
    }

    // --- manuals + chunks (RAG index) -----------------------------------
    private static async Task SeedManualsAsync(
        FactoryBrainDbContext db, IRagService rag, IEmbeddingService embedder, EmbeddingColumnAdmin admin, CancellationToken ct)
    {
        if (await db.ManualDocuments.AnyAsync(ct)) return;

        var seeds = new (string TitleEn, string TitleBn, string[] Tags, string BodyEn, string BodyBn, string Source, string Dept, string Cat)[]
        {
            ("Bearing care checklist (sewing machines)",
             "বিয়ারিং কেয়ার চেকলিস্ট (সেলাই মেশিন)",
             new[]{"maintenance","sewing","bearing"},
             "Weekly: check bearing temperature and vibration RMS with the handheld probe. Quarterly: re-grease with NLGI #2 lithium. Replace the bearing when vibration exceeds 4.5 mm/s or temperature crosses 75 °C for more than 30 minutes.",
             "সাপ্তাহিক: হ্যান্ডহেল্ড প্রোব দিয়ে বিয়ারিং তাপমাত্রা এবং কম্পন RMS পরীক্ষা করুন। ত্রৈমাসিক: NLGI #২ লিথিয়াম দিয়ে পুনর্গ্রিস করুন।",
             "manual","sewing","manuals"),
            ("Line 3 efficiency decline — investigations",
             "লাইন ৩ দক্ষতা হ্রাস — তদন্ত",
             new[]{"line-3","efficiency","investigation"},
             "Sustained 12–18% dip vs target over the last 4 hours. Check sewing helper allocation first, then QC backlog. Sensor log pattern: bundle_scan rate dropped from 18 to 11/min between 09:00–11:00.",
             "গত ৪ ঘন্টায় লক্ষ্যের তুলনায় টানা ১২–১৮% হ্রাস। প্রথমে সেলাই সহায়ক বরাদ্দ, তারপর QC ব্যাকলগ পরীক্ষা করুন।",
             "sensor_log","sewing","manuals"),
            ("Buttonhole machine — calibration",
             "বোতামের ছিদ্র মেশিন — ক্যালিব্রেশন",
             new[]{"finishing","buttonhole","calibration"},
             "Cutter blade tension target: 2.4 N·m. Re-calibrate after every 5,000 cycles. When binding rate exceeds 2% over a shift, pull two random samples and inspect under the loupe.",
             "কাটার ব্লেড টেনশন লক্ষ্য: ২.৪ N·m। প্রতি ৫,০০০ সাইকেলের পর পুনঃক্যালিব্রেট করুন।",
             "manual","finishing","manuals"),
            ("Bundles per minute — target table by SMV",
             "বান্ডল প্রতি মিনিট — SMV অনুযায়ী লক্ষ্য",
             new[]{"throughput","sah","smv"},
             "Standard SAH target: 22 bundles/min at SMV 0.45, scaling linearly to 13 bundles/min at SMV 0.75. If observed rate is below 80% of target for 30+ minutes, pull helper allocation from Finishing and rebalance to Sewing.",
             "স্ট্যান্ডার্ড SAH লক্ষ্য: SMV ০.৪৫ এ ২২ বান্ডল/মিনিট, SMV ০.৭৫ এ রৈখিকভাবে ১৩ বান্ডল/মিনিট।",
             "manual","sewing","manuals"),
            ("Energy spike on Line 4 compressor — log",
             "লাইন ৪ কম্প্রেসর শক্তি স্পাইক — লগ",
             new[]{"line-4","energy","compressor"},
             "Energy meter on compressor Line 4: 22% above 7-day rolling average. Peak demand 165 kW vs baseline 130 kW. Pattern starts after 14:00 daily — correlate with finishing-station dryer cycle.",
             "কম্প্রেসর লাইন ৪ এর শক্তি মিটার: ৭ দিনের চলমান গড়ের চেয়ে ২২% বেশি। পিক ডিমান্ড ১৬৫ kW বনাম বেসলাইন ১৩০ kW।",
             "sensor_log","sewing","sop"),
            ("QC reject categories & thresholds",
             "QC প্রত্যাখ্যান বিভাগ ও থ্রেশহোল্ড",
             new[]{"qc","rejects","defects"},
             "Reject categories: stitch skip (> 3 mm gap), open seam (> 5 mm), oil stain (> 5 mm), shading, measurement out-of-tolerance (> 4 mm). If a category spikes by >25% week-on-week, file a defect cluster insight and reroute the next two PO bundles to inline QC.",
             "প্রত্যাখ্যান বিভাগ: সেলাই বাদ (> ৩ মিমি ফাঁক), খোলা সিম (> ৫ মিমি), তেলের দাগ (> ৫ মিমি)।",
             "manual","qc","qc"),
            ("Cutting table — fabric relaxation time",
             "কাটিং টেবিল — কাপড় রিল্যাক্সেশন সময়",
             new[]{"cutting","fabric","preparation"},
             "Allow knit fabric to relax for 12–24 hours before cutting. Shrinkage of 3–5% is expected. Spreading on the cutting table: align selvage, face up for the top ply, and pre-spot any knot/stain.",
             "কাটিংয়ের আগে নিট কাপড়কে ১২–২৪ ঘন্টা রিল্যাক্স করতে দিন। ৩–৫% সংকোচন প্রত্যাশিত।",
             "manual","cutting","sop"),
            ("M-101 vibration trend — last 24h",
             "M-101 কম্পন প্রবণতা — শেষ ২৪ ঘন্টা",
             new[]{"m-101","machine","vibration","investigation"},
             "Vibration RMS on M-101 climbed from 2.1 mm/s (06:00) to 3.9 mm/s (17:00). Temperature stable at 64 °C. Noise floor unchanged. Likely cause: feed dog wear — schedule bearing inspection within 72h.",
             "M-101 এ কম্পন RMS ০৬:০০ এ ২.১ mm/s থেকে ১৭:০০ এ ৩.৯ mm/s এ উঠেছে। তাপমাত্রা ৬৪°C এ স্থিতিশীল।",
             "sensor_log","sewing","manuals"),
        };

        int idx = 1;
        foreach (var s in seeds)
        {
            var docId = $"doc-{idx++}";
            var doc = new ManualDocument
            {
                Id = docId,
                TitleEn = s.TitleEn,
                TitleBn = s.TitleBn,
                Tags = s.Tags.ToList(),
                BodyEn = s.BodyEn,
                BodyBn = s.BodyBn,
                Source = s.Source,
                Department = s.Dept,
                Category = s.Cat,
                // Stamp the active embedder so NeedsReindexAsync can detect drift later.
                EmbeddingProvider = embedder.ProviderId,
                EmbeddingModel    = embedder.ModelId,
                Dims              = embedder.Dimensions
            };
            var chunks = await rag.ChunkAsync(docId, s.TitleEn, s.BodyEn, s.Tags, s.Dept, s.Cat, ct);
            bool anyChunkMissing = false;
            foreach (var c in chunks)
            {
                // Dimension guard: a freshly-computed vector whose length
                // disagrees with the live column dim is dropped (chunk
                // stored with Embedding=null, row stamped "pending"); a
                // future reindex will fill it in.
                c.Embedding = admin.GuardVectorDims(c.Embedding, doc);
                if (c.Embedding is null) anyChunkMissing = true;
                doc.Chunks.Add(c);
            }
            if (anyChunkMissing)
            {
                // Mirror the row metadata flip on the doc level too so the
                // pending flag is consistent for /api/rag/status.
                doc.EmbeddingProvider = "pending";
                doc.EmbeddingModel    = "pending";
                doc.Dims              = 0;
            }
            db.ManualDocuments.Add(doc);
        }
        await db.SaveChangesAsync(ct);
    }

    // --- RMG demo corpus (idempotent) -------------------------------------
    /// <summary>
    /// Idempotent RMG demo corpus. Every row has <c>IsDemo = true</c> and
    /// either a title prefix or a tag of <c>Demo</c> so the UI / list API
    /// can mark them clearly. Seeds a needle-breakage SOP, AQL 1.5 +
    /// AQL 2.5 sampling tables, Juki / Brother lockstitch error-code
    /// tables (clearly labelled demo, not vendor manuals), an
    /// ACCORD / RSC-style fire + electrical safety checklist, SMV + DHU
    /// definitions with a worked example, and a Bangla FAQ of 10 Q&amp;A
    /// pairs covering line efficiency, needle breakage and AQL.
    ///
    /// <para>
    /// Idempotency is per-row: each doc has a stable id
    /// (<c>demo-needle-sop</c> etc.); rows that already exist are
    /// skipped on every subsequent boot, so re-running the API does
    /// not duplicate chunks.
    /// </para>
    /// </summary>
    private static async Task SeedRmgDemoCorpusAsync(
        FactoryBrainDbContext db, IRagService rag, EmbeddingColumnAdmin admin, CancellationToken ct)
    {
        var seeds = new[]
        {
            DemoNeedleBreakageSOP(),
            DemoAql15Table(),
            DemoAql25Table(),
            DemoJukiErrorTable(),
            DemoBrotherErrorTable(),
            DemoAccordFireSafety(),
            DemoSmvDhuDefinition(),
            DemoBanglaFaq(),
        };

        int added = 0;
        foreach (var seed in seeds)
        {
            if (await db.ManualDocuments.AnyAsync(d => d.Id == seed.Id, ct)) continue;
            var chunks = await rag.ChunkAsync(
                seed.Id, seed.Title, seed.Body, seed.Tags, seed.Department, seed.Category, ct);
            var doc = new ManualDocument
            {
                Id = seed.Id,
                TitleEn = seed.Title,
                TitleBn = string.Empty,
                Tags = seed.Tags,
                BodyEn = seed.Body,
                BodyBn = string.Empty,
                Source = seed.Source,
                Department = seed.Department,
                Category = seed.Category,
                Url = null,
                CreatedAt = DateTime.UtcNow,
                IsDemo = true,
                // The active embedder stamps the row's metadata so the
                // next NeedsReindexAsync check picks up drift. In
                // degraded mode the chunk's Embedding is null and the
                // row is marked pending; the next non-degraded reindex
                // fills the vectors.
                EmbeddingProvider = seed.ActiveProvider ?? "local",
                EmbeddingModel    = seed.ActiveModel    ?? "hash-md5",
                Dims              = seed.ActiveDims
            };
            bool anyChunkMissing = false;
            foreach (var c in chunks)
            {
                // Same dimension guard as SeedManualsAsync — a vector
                // whose length disagrees with the live column dim is
                // dropped and the row is flipped to "pending" so a
                // later reindex can fill the gap.
                c.Embedding = admin.GuardVectorDims(c.Embedding, doc);
                if (c.Embedding is null) anyChunkMissing = true;
                doc.Chunks.Add(c);
            }
            if (anyChunkMissing)
            {
                doc.EmbeddingProvider = "pending";
                doc.EmbeddingModel    = "pending";
                doc.Dims              = 0;
            }
            db.ManualDocuments.Add(doc);
            added++;
        }
        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private sealed record DemoSeed(
        string Id,
        string Title,
        string Body,
        List<string> Tags,
        string Source,
        string Department,
        string Category,
        string? ActiveProvider = null,
        string? ActiveModel    = null,
        int     ActiveDims     = 384);

    private static DemoSeed DemoNeedleBreakageSOP() => new(
        Id: "demo-needle-breakage-sop",
        Title: "Demo · Needle-breakage SOP (lockstitch machines)",
        Body:
            "# Needle-breakage SOP — lockstitch machines (DEMO, not a vendor manual)\n\n" +
            "This demo SOP lists the top causes of needle breakage on lockstitch heads (Juki DDL-8700, " +
            "Brother DB2-B755) and the field fixes mechanics apply first. Always isolate the machine " +
            "electrically before touching the needle bar.\n\n" +
            "## 1. Cause → fix table\n\n" +
            "- Wrong needle size for fabric: switch to Nm 70 for light knits (180–220 gsm), Nm 80 for " +
            "  mid-weight (220–280 gsm), Nm 90 for denim (320+ gsm). Needle DBx1 / 134 family only.\n" +
            "- Needle inserted backwards (long groove faces the bobbin): the long groove must face the " +
            "  operator side. Reverse and re-thread.\n" +
            "- Needle installed too high: the top of the eye should sit ~1 mm below the hook point at " +
            "  needle-down position. Re-set with the standard gauge.\n" +
            "- Burr on the needle plate or bobbin hook: replace the plate; stone the hook lightly with " +
            "  3000-grit, then re-lap with sewing-machine oil.\n" +
            "- Tight thread tension (top > 3.5 N or bobbin > 2.5 N): drop both by 0.3 N, run five " +
            "  stitches, re-measure.\n" +
            "- Worn needle bar / bent needle bar: replace the bar. Re-check parallelism with the hook.\n\n" +
            "## 2. Quick checks before changing the needle\n\n" +
            "1. Pull the fabric path taut by hand and look for the broken-needle tip in the feed dog.\n" +
            "2. Count broken bits in the bobbin case — a third piece means a guard is missing.\n" +
            "3. Run the head at 200 RPM with no fabric for 10 s; listen for metallic click = hook strike.\n\n" +
            "## 3. Documenting the fix\n\n" +
            "Open an Insight with title \"Needle breakage — {line} {head}\", attach the head's vibration " +
            "RMS (last 30 min) and the fabric lot, and route to maintenance. SMV impact: a single " +
            "broken needle averages 90–180 seconds of downtime, so a spike of 5+ breakages/shift is " +
            "a 12–25 minute efficiency drag worth an investigation card.\n\n" +
            "Tags: demo, sewing, needle, juki ddl-8700, brother db2-b755, smv 0.45, smv 0.55.",
        Tags: new() { "demo", "sewing", "needle", "juki ddl-8700", "brother db2-b755", "smv:0.45", "smv:0.55" },
        Source: "sop", Department: "sewing", Category: "sop");

    private static DemoSeed DemoAql15Table() => new(
        Id: "demo-aql-1.5-sampling",
        Title: "Demo · AQL 1.5 sampling — single sampling plan (normal inspection)",
        Body:
            "# AQL 1.5 sampling plan (DEMO)\n\n" +
            "Acceptance Quality Limit 1.5% is the most common cut for premium / safety-critical " +
            "shipments (children's wear, technical outerwear, intimate apparel). Use the locked " +
            "tables below for normal inspection; switch to tightened when 2 of any 5 consecutive " +
            "lots are rejected, and to reduced when 5 of 5 are accepted.\n\n" +
            "## Sample sizes and accept / reject numbers (AQL 1.5, normal inspection)\n\n" +
            "- Lot 26–50: sample 8, accept 0, reject 1.\n" +
            "- Lot 51–90: sample 13, accept 0, reject 1.\n" +
            "- Lot 91–150: sample 20, accept 0, reject 1.\n" +
            "- Lot 151–280: sample 32, accept 1, reject 2.\n" +
            "- Lot 281–500: sample 50, accept 1, reject 2.\n" +
            "- Lot 501–1200: sample 80, accept 2, reject 3.\n" +
            "- Lot 1201–3200: sample 125, accept 3, reject 4.\n" +
            "- Lot 3201–10000: sample 200, accept 5, reject 6.\n" +
            "- Lot 10001–35000: sample 315, accept 7, reject 8.\n\n" +
            "## Reading the table\n\n" +
            "Sample size is the number of units pulled at random from the lot. Accept = the number " +
            "of defectives allowed; reject = the number that flips the lot to a reject decision. AQL " +
            "1.5 means up to 1.5% defective is acceptable in the long run.\n\n" +
            "## When to switch plans\n\n" +
            "Switch to tightened (move one row down the table) after 2 of 5 consecutive lots are " +
            "rejected on original inspection. Switch back to normal after 5 accepted lots on " +
            "tightened. Use skip-lot only after 10 consecutive accepted lots on tightened.",
        Tags: new() { "demo", "qc", "aql", "aql:1.5", "compliance" },
        Source: "sop", Department: "qc", Category: "qc");

    private static DemoSeed DemoAql25Table() => new(
        Id: "demo-aql-2.5-sampling",
        Title: "Demo · AQL 2.5 sampling — single sampling plan (normal inspection)",
        Body:
            "# AQL 2.5 sampling plan (DEMO)\n\n" +
            "AQL 2.5% is the default for general apparel export. Use the tables below for normal " +
            "inspection; switch to tightened or reduced per the switching rules in the AQL 1.5 " +
            "document. The same sample-size brackets apply; only the accept / reject numbers change.\n\n" +
            "## Sample sizes and accept / reject numbers (AQL 2.5, normal inspection)\n\n" +
            "- Lot 26–50: sample 8, accept 0, reject 1.\n" +
            "- Lot 51–90: sample 13, accept 0, reject 1.\n" +
            "- Lot 91–150: sample 20, accept 0, reject 1.\n" +
            "- Lot 151–280: sample 32, accept 1, reject 2.\n" +
            "- Lot 281–500: sample 50, accept 2, reject 3.\n" +
            "- Lot 501–1200: sample 80, accept 3, reject 4.\n" +
            "- Lot 1201–3200: sample 125, accept 5, reject 6.\n" +
            "- Lot 3201–10000: sample 200, accept 7, reject 8.\n" +
            "- Lot 10001–35000: sample 315, accept 10, reject 11.\n\n" +
            "## Common pitfalls\n\n" +
            "1. Pulling the sample from the top of a bale — always pull from at least three layers.\n" +
            "2. Reading the lot code instead of the lot size — verify physical bale count before " +
            "   selecting the row.\n" +
            "3. Recording defects as 'minor' when the buyer specifies major/minor split — both must " +
            "   be counted against AQL.\n\n" +
            "## Documentation\n\n" +
            "Open a QC inspection card with lot code, sample size, accept / reject numbers, defect " +
            "category breakdown (stitch skip, open seam, oil stain, shading, measurement), and the " +
            "inspector's badge id. Route the card to the floor QC supervisor before disposition.",
        Tags: new() { "demo", "qc", "aql", "aql:2.5", "compliance" },
        Source: "sop", Department: "qc", Category: "qc");

    private static DemoSeed DemoJukiErrorTable() => new(
        Id: "demo-juki-error-codes",
        Title: "Demo · Juki lockstitch error-code table (DDL-8700 family)",
        Body:
            "# Juki lockstitch error-code table — DDL-8700 family (DEMO)\n\n" +
            "This is a demo, condensed reference for the most common Juki DDL-8700 / DDL-9000C " +
            "lockstitch alarms. Always confirm with the vendor manual before changing boards or " +
            "stepper drives.\n\n" +
            "## E-01 — needle-up position not detected\n\n" +
            "- Cause: needle position sensor misaligned or covered in lint.\n" +
            "- Fix: clean the sensor with a dry brush, then re-seat the connector on the control " +
            "  board. Run a needle-up / needle-down cycle and verify the LED on the sensor flips.\n\n" +
            "## E-02 — thread trimmer blade timeout\n\n" +
            "- Cause: trim cam out of phase, or blade solenoid sticking.\n" +
            "- Fix: reset the trim cam with the handwheel (align the yellow dot to the index mark), " +
            "  then run the head at 200 RPM for 10 s. If the alarm persists, replace the solenoid.\n\n" +
            "## E-12 — bobbin-winder over-current\n\n" +
            "- Cause: bobbin wound too tightly, or winder spring fatigued.\n" +
            "- Fix: discard the over-tight bobbin, loosen the winder spring tension by one click, " +
            "  rewind. If the alarm repeats on a fresh bobbin, replace the winder assembly.\n\n" +
            "## ALARM — foot-lift sensor stuck\n\n" +
            "- Cause: foot-lift pedal lever loose, or sensor cable chafed at the pivot.\n" +
            "- Fix: re-tension the pedal return spring, inspect the cable at the pivot, replace if " +
            "  the outer jacket is cracked.\n\n" +
            "## FAULT — stepper drive over-temperature\n\n" +
            "- Cause: cooling fan filter blocked, ambient > 38 °C, or stitch rate held at 4500 RPM.\n" +
            "- Fix: clean the fan filter, drop the programmed max stitch rate to 4000 RPM, verify " +
            "  ambient with the floor thermometer. Replace the drive only after the above is " +
            "  confirmed.",
        Tags: new() { "demo", "sewing", "juki ddl-8700", "error-code" },
        Source: "sop", Department: "sewing", Category: "sop");

    private static DemoSeed DemoBrotherErrorTable() => new(
        Id: "demo-brother-error-codes",
        Title: "Demo · Brother lockstitch error-code table (BAS-311H / DB2-B755)",
        Body:
            "# Brother lockstitch error-code table — BAS-311H / DB2-B755 (DEMO)\n\n" +
            "Demo, condensed reference for the most common Brother lockstitch alarms. Confirm with " +
            "the vendor manual before swapping control boards.\n\n" +
            "## Err-401 — bobbin thread exhaustion\n\n" +
            "- Cause: bobbin ran out mid-seam; rarely a false trip from a loose bobbin case.\n" +
            "- Fix: insert a fresh bobbin (class L for BAS-311H, class SA for DB2-B755), re-thread " +
            "  the bobbin case, run three stitches and verify the under-thread tension reads " +
            "  between 0.20–0.25 N on the gauge.\n\n" +
            "## E-01 — needle bar over-travel\n\n" +
            "- Cause: needle clamp loose, or hook timing out of spec.\n" +
            "- Fix: power-cycle, handwheel the head to needle-down, verify the needle bar clamp is " +
            "  torqued to 1.8 N·m. Re-check hook timing with the 0.8 mm gauge; adjust if needed.\n\n" +
            "## E-02 — presser-foot lift sensor fault\n\n" +
            "- Cause: sensor magnet demagnetised (typical after 4+ years of thermal cycling).\n" +
            "- Fix: replace the presser-foot lift sensor assembly (Brother PN S-37120-001). Run " +
            "  five lift cycles and confirm the LED indicator flips twice per cycle.\n\n" +
            "## ALARM — thread-break upper\n\n" +
            "- Cause: thread guide eyelet misaligned, top tension too high, or thread lot " +
            "  contaminated with lint.\n" +
            "- Fix: re-thread through every guide, drop the top tension to 2.5 N, swap to a fresh " +
            "  thread cone. If alarms repeat with two thread lots in a row, inspect the rotary " +
            "  tension disc for nicks.\n\n" +
            "## FAULT — main shaft encoder skip\n\n" +
            "- Cause: encoder disc dirty, or belt slip between the motor and the main shaft.\n" +
            "- Fix: clean the encoder disc with isopropyl alcohol, re-tension the belt to 35 N " +
            "  deflection, run a 200-RPM idle for 30 s. Replace the encoder only after both fail.",
        Tags: new() { "demo", "sewing", "brother bas-311h", "error-code" },
        Source: "sop", Department: "sewing", Category: "sop");

    private static DemoSeed DemoAccordFireSafety() => new(
        Id: "demo-accord-fire-safety-checklist",
        Title: "Demo · ACCORD / RSC fire & electrical safety checklist",
        Body:
            "# ACCORD / RSC fire & electrical safety checklist (DEMO)\n\n" +
            "Demo checklist inspired by the Accord and RSC fire, electrical and building safety " +
            "programmes for Bangladesh RMG factories. Always refer to the latest Accord / RSC " +
            "guidance before signing off an audit; this is a working aid, not a substitute.\n\n" +
            "## 1. Fire detection and alarm\n\n" +
            "- Addressable smoke detectors in every cutting, sewing and finishing hall, max 9 m " +
            "  spacing on straight runs.\n" +
            "- Manual call points at every stair tower exit, max 45 m apart.\n" +
            "- Alarm sounders audible at 65 dB(A) at every workstation; tested weekly with a " +
            "  logged drill.\n" +
            "- Fire pump room on a dedicated circuit; jockey pump runs continuously, main pump " +
            "  cuts in within 30 s of pressure drop.\n\n" +
            "## 2. Means of egress\n\n" +
            "- Two remote exits from every hall; travel distance max 30 m.\n" +
            "- Exit signs lit continuously, even on power loss (battery backup ≥ 90 minutes).\n" +
            "- Stair towers enclosed in 2-hour fire-rated construction; no storage under the " +
            "  stairs.\n" +
            "- Exit doors open in the direction of egress, no locks requiring keys or special " +
            "  knowledge during occupancy.\n\n" +
            "## 3. Electrical safety\n\n" +
            "- Main panel labelled, every breaker labelled, no double-lugged breakers.\n" +
            "- ELCB / RCCB on every socket-outlet circuit; trip current 30 mA for general, 10 mA " +
            "  for wet areas.\n" +
            "- Cable trays off the floor; no cables through doorways or under rugs.\n" +
            "- Earthing continuity tested annually; record on every machine's maintenance card.\n\n" +
            "## 4. Fire suppression\n\n" +
            "- Wet-riser / hydrant system sized for 4500 l/min at the most remote hydrant.\n" +
            "- Sprinkler coverage in storage racks ≥ 7.5 m high, K-factor matched to commodity.\n" +
            "- Portable extinguishers: CO₂ for electrical, dry powder for finished-goods storage, " +
            "  water-mist for cutting halls.\n\n" +
            "## 5. Documentation\n\n" +
            "- Monthly self-audit signed by the factory manager and the safety officer.\n" +
            "- Quarterly third-party audit by an Accord / RSC approved firm.\n" +
            "- Every corrective action with a 30-day due date logged in the safety register.\n",
        Tags: new() { "demo", "compliance", "fire-safety", "electrical", "accord", "rsc" },
        Source: "compliance", Department: "general", Category: "compliance");

    private static DemoSeed DemoSmvDhuDefinition() => new(
        Id: "demo-smv-dhu-definition",
        Title: "Demo · SMV & DHU definitions with a worked example",
        Body:
            "# SMV & DHU definitions (DEMO)\n\n" +
            "Two of the most-quoted production-engineering numbers in apparel — SMV and DHU — " +
            "mean very different things and are easy to swap. This is a working definition, not a " +
            "substitute for the buyer-specific manuals.\n\n" +
            "## SMV — Standard Minute Value\n\n" +
            "SMV is the time, in standard minutes, that a qualified operator working at 100% " +
            "efficiency takes to complete one garment on one operation. It is the building block " +
            "for line balancing and SAH (Standard Allowed Hours) calculation. Typical knit-top SMV " +
            "ranges:\n\n" +
            "- SMV 0.45 — light polo tee, basic side seam + hem.\n" +
            "- SMV 0.55 — polo with placket, cuff attach.\n" +
            "- SMV 0.65 — full-fashion tee with neck rib and 2-needle coverstitch hem.\n" +
            "- SMV 0.75 — hoodie with kangaroo pocket and hood attach.\n\n" +
            "## DHU — Defects per Hundred Units\n\n" +
            "DHU counts the number of defects found per 100 garments inspected (or produced, " +
            "depending on the buyer). It is the inverse of quality yield: a 28 DHU line produces " +
            "28 defects per 100 garments inspected, i.e. a quality yield of 72%.\n\n" +
            "## Worked example\n\n" +
            "An 8-hour shift, 30 operators, producing polo tees with SMV 0.55.\n\n" +
            "1. Total available minutes = 30 operators × 8 h × 60 min × efficiency.\n" +
            "2. At 75% line efficiency: 30 × 8 × 60 × 0.75 = 10 800 standard minutes available.\n" +
            "3. Output at SAH = available / SMV = 10 800 / 0.55 = 19 636 garments.\n" +
            "4. Bundles per minute (assuming 10 garments / bundle) = 19 636 / (8 h × 60 min × 10) " +
            "   = 4.09 bundles / min, well below the SAH 22 target for SMV 0.45 lines.\n" +
            "5. QC inspects 800 garments, finds 24 defects: DHU = (24 / 800) × 100 = 3.0.\n\n" +
            "The line is running well below throughput target and quality is healthy. The first " +
            "lever to pull is operator balance, not quality — at this DHU the rework cost is " +
            "negligible compared to the throughput gap.",
        Tags: new() { "demo", "production-engineering", "smv", "dhu", "sah" },
        Source: "manual", Department: "general", Category: "sop");

    private static DemoSeed DemoBanglaFaq() => new(
        Id: "demo-bangla-rag-faq",
        Title: "Demo · বাংলা FAQ — লাইন দক্ষতা, সুই ভাঙা ও AQL",
        Body:
            "# বাংলা FAQ — লাইন দক্ষতা, সুই ভাঙা ও AQL (DEMO)\n\n" +
            "নিচে বাংলায় প্রায়শই জিজ্ঞাসিত প্রশ্ন ও উত্তরগুলো দেওয়া হলো। এগুলো শুধুমাত্র " +
            "ডেমো — চূড়ান্ত সিদ্ধান্তের আগে সুপারভাইজার বা কোয়ালিটি লিডের সাথে যাচাই করুন।\n\n" +
            "## ১. লাইন দক্ষতা কীভাবে বাড়াবো?\n" +
            "প্রথমে বটলনেক অপারেশন চিহ্নিত করুন। SAH লক্ষ্যের সাথে প্রকৃত আউটপুট তুলনা করুন। " +
            "বান্ডল স্ক্যান রেট, সুই ব্রেকেজ ও কিউসি ব্যাকলগ একসাথে দেখুন। সাধারণত সেলাই সহায়ক " +
            "বরাদ্দ ঠিক করলেই ৫–১০% দক্ষতা ফিরে পাওয়া যায়।\n\n" +
            "## ২. SMV কী?\n" +
            "SMV হলো Standard Minute Value — একজন যোগ্য অপারেটর ১০০% দক্ষতায় একটি গার্মেন্টের " +
            "একটি অপারেশন সম্পন্ন করতে যে সময় নেয়, সেটি standard minute এ। লাইন ব্যালান্সিং ও " +
            "SAH হিসাবের ভিত্তি এটি।\n\n" +
            "## ৩. DHU কী?\n" +
            "DHU হলো Defects per Hundred Units — প্রতি ১০০ গার্মেন্টে কতটি ডিফেক্ট পাওয়া " +
            "গেছে। ২৮ DHU মানে ১০০ গার্মেন্টে ২৮টি ডিফেক্ট, বা কোয়ালিটি ইয়েল্ড ৭২%।\n\n" +
            "## ৪. সুই বারবার ভাঙছে, প্রথমে কী দেখব?\n" +
            "প্রথমে সুই সাইজ ও ফ্যাব্রিকের মিল দেখুন — হালকা নিটে Nm 70, মিড-ওয়েটে Nm 80, " +
            "ডেনিমে Nm 90। সুই বিপরীত দিকে ঢোকানো আছে কিনা দেখুন (লম্বা খাঁজ অপারেটরের দিকে " +
            "হতে হবে)। তারপর সুই প্লেট ও ববিন হুকে burr আছে কিনা পরীক্ষা করুন।\n\n" +
            "## ৫. AQL 1.5 আর AQL 2.5 এর পার্থক্য কী?\n" +
            "AQL 1.5 বেশি কঠোর — দীর্ঘমেয়াদে ১.৫% পর্যন্ত ডিফেক্ট গ্রহণযোগ্য। প্রিমিয়াম ও " +
            "নিরাপত্তা-সংক্রান্ত পণ্যে (শিশুদের পোশাক, ইন্টিমেট অ্যাপারেল) এটি ব্যবহার করা হয়। " +
            "AQL 2.5 হলো সাধারণ অ্যাপারেল এক্সপোর্টের ডিফল্ট। স্যাম্পল সাইজের ব্র্যাকেট একই, " +
            "শুধু accept / reject সংখ্যা আলাদা।\n\n" +
            "## ৬. প্রিমিয়াম শিপমেন্টে কোন AQL?\n" +
            "সাধারণত AQL 1.5 ব্যবহার করা হয়। বায়ার চুক্তিতে অন্য কিছু উল্লেখ না থাকলে AQL " +
            "1.5, normal inspection ডিফল্ট।\n\n" +
            "## ৭. বান্ডল প্রতি মিনিট লক্ষ্য কীভাবে বের করব?\n" +
            "SAH লক্ষ্য দিয়ে শুরু করুন — SMV 0.45 তে SAH 22 bundles/min, SMV 0.75 এ SAH 13 " +
            "bundles/min। লক্ষ্যের ৮০% এর নিচে ৩০+ মিনিট থাকলে সহায়ক বরাদ্দ পুনর্বণ্টন করুন।\n\n" +
            "## ৮. সুই ভাঙলে ডাউনটাইম কত?\n" +
            "একটি সুই ভাঙলে গড়ে ৯০–১৮০ সেকেন্ড ডাউনটাইম হয়। এক শিফটে ৫+ সুই ভাঙা মানে ১২–২৫ " +
            "মিনিট দক্ষতা হারানো — এটি একটি তদন্ত কার্ড খোলার মতো ঘটনা।\n\n" +
            "## ৯. AQL পাস করলে কি সব গার্মেন্ট ছাড়া যাবে?\n" +
            "হ্যাঁ, AQL পাস মানে লটটি গ্রহণযোগ্য। তবে বায়ার চুক্তিতে বিশেষ কোনো শর্ত থাকলে " +
            "(যেমন critical defects zero tolerance) সেগুলো আলাদাভাবে দেখতে হবে।\n\n" +
            "## ১০. লাইন ৩ এ দক্ষতা কমে গেলে প্রথমে কী করব?\n" +
            "প্রথমে সেলাই সহায়ক বরাদ্দ দেখুন — বান্ডল স্ক্যান রেট কমে গেছে কিনা যাচাই করুন। " +
            "তারপর কিউসি ব্যাকলগ ও ফিনিশিং স্টেশনের ড্রায়ার সাইকেল দেখুন। সাধারণত এই তিনটি " +
            "জায়গায় ৯০% ক্ষেত্রে সমাধান পাওয়া যায়।",
        Tags: new() { "demo", "faq", "bangla", "line-efficiency", "needle", "aql" },
        Source: "faq", Department: "general", Category: "policy");

    // --- line-board -------------------------------------------------------
    private static async Task SeedLineBoardAsync(FactoryBrainDbContext db, CancellationToken ct)
    {
        if (await db.LineBoardMetrics.AnyAsync(ct)) return;
        var rng = new Random(0xBEEF);
        var seeds = new (string Id, string Name, int Target)[]
        {
            ("line-1", "Line 1 — Polo Tee", 70),
            ("line-2", "Line 2 — Crew Neck", 70),
            ("line-3", "Line 3 — V-Neck", 78),
            ("line-4", "Line 4 — Hoodie", 70),
            ("line-5", "Line 5 — Tank Top", 70),
            ("line-6", "Line 6 — Jacket", 70),
        };
        foreach (var s in seeds)
        {
            int eff = 50 + rng.Next(40);
            int wip = 100 + rng.Next(220);
            int npt = 10 + rng.Next(60);
            var bn = (eff < 50 || s.Target - eff >= 10) ? Bottleneck.Red :
                     (s.Target - eff >= 5) ? Bottleneck.Amber : Bottleneck.Green;
            db.LineBoardMetrics.Add(new LineBoardMetric
            {
                Id = s.Id,
                Name = s.Name,
                EfficiencyPct = eff,
                SahTarget = s.Target,
                SahActual = eff,
                WipBundles = wip,
                Bottleneck = bn,
                NptMinutes = npt,
                UpdatedAt = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync(ct);
    }

    // --- sensors ----------------------------------------------------------
    private static async Task SeedSensorReadingsAsync(FactoryBrainDbContext db, CancellationToken ct)
    {
        if (await db.SensorReadings.AnyAsync(ct)) return;
        var rng = new Random(0xC0FFEE);
        var profile = new (SensorSource Source, string Metric, string Unit, double Lo, double Hi, Func<Random, string> Entity)[]
        {
            (SensorSource.Rfid,      "scans_per_min", "scans/min", 8,   22,  _ => Pick(rng, "gate-A1","gate-A2","gate-B1","gate-C1")),
            (SensorSource.Rfid,      "miss_rate",     "%",         0,    6,  _ => Pick(rng, "gate-A1","gate-A2","gate-B1","gate-C1")),
            (SensorSource.Telemetry, "vibration_rms", "mm/s",     0.8, 4.5, _ => Pick(rng, "M-101","M-102","M-103","M-201","M-202","M-301","M-302")),
            (SensorSource.Telemetry, "temperature_c", "°C",       45,  82,  _ => Pick(rng, "M-101","M-102","M-103","M-201","M-202","M-301","M-302")),
            (SensorSource.Telemetry, "duty_cycle",    "%",        55,  95,  _ => Pick(rng, "M-101","M-102","M-103","M-201","M-202","M-301","M-302")),
            (SensorSource.Energy,    "kwh",           "kWh",      42,  78,  _ => Pick(rng, "meter:floor-1","meter:floor-2","meter:floor-3")),
            (SensorSource.Energy,    "peak_demand",   "kW",       110, 165, _ => Pick(rng, "meter:floor-1","meter:floor-2","meter:floor-3")),
        };

        var now = DateTime.UtcNow;
        int id = 1;
        for (int t = 0; t < 24; t++)
        {
            var ts = now.AddMinutes(-t);
            for (int k = 0; k < 3; k++)
            {
                var p = profile[rng.Next(profile.Length)];
                db.SensorReadings.Add(new SensorReading
                {
                    Id = $"seed-sensor-{id++}",
                    Source = p.Source,
                    EntityId = p.Entity(rng),
                    Metric = p.Metric,
                    Value = Math.Round(p.Lo + rng.NextDouble() * (p.Hi - p.Lo), 2),
                    Unit = p.Unit,
                    Ts = ts,
                    SimTick = t
                });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // --- QC defects (deterministic per-cell) ------------------------------
    private static readonly string[] Operations =
        { "cutting", "sewing", "buttonhole", "top_stitch", "qc_inspection", "finishing" };
    private static readonly string[] Lines =
        { "line-1", "line-2", "line-3", "line-4", "line-5", "line-6" };

    private static async Task SeedQcDefectsAsync(FactoryBrainDbContext db, CancellationToken ct)
    {
        if (await db.QcDefects.AnyAsync(ct)) return;

        var weekStarts = RecentWeekStarts(8);
        foreach (var op in Operations)
        {
            for (int lineIdx = 0; lineIdx < Lines.Length; lineIdx++)
            {
                var line = Lines[lineIdx];
                var rng = new Random((int)(0xDEFE0001 ^ (Array.IndexOf(Operations, op) * 0x9E37) ^ (lineIdx * 0x7F4A)));
                int inspTot = 0, defTot = 0, majTot = 0, minTot = 0, rewTot = 0;
                var cell = new QcDefect
                {
                    Id = $"{op}-{line}",
                    Operation = op,
                    LineId = line
                };
                for (int w = 0; w < weekStarts.Length; w++)
                {
                    int inspected = 1400 + (w % 4) * 40 + rng.Next(200) - (w * 17) % 60;
                    if (inspected < 0) inspected = 0;
                    double defectRate = Math.Max(0.005, 0.012 + Array.IndexOf(Operations, op) * 0.0037 + lineIdx * 0.0021 + (rng.NextDouble() - 0.5) * 0.012);
                    int defects = Math.Min(inspected, (int)Math.Round(inspected * defectRate));
                    double majorShare = Math.Clamp(0.30 + (rng.NextDouble() - 0.5) * 0.15, 0.05, 0.95);
                    int major = (int)Math.Round(defects * majorShare);
                    int minor = Math.Max(0, defects - major);
                    int rework = Math.Min(defects, (int)Math.Round(defects * (0.25 + (rng.NextDouble() - 0.5) * 0.1)));

                    cell.Weeks.Add(new QcDefectWeek
                    {
                        WeekStart = weekStarts[w],
                        Inspected = inspected,
                        Defects = defects,
                        Major = major,
                        Minor = minor,
                        Rework = rework
                    });
                    inspTot += inspected; defTot += defects; majTot += major; minTot += minor; rewTot += rework;
                }
                cell.InspectedTotal = inspTot;
                cell.DefectsTotal = defTot;
                cell.MajorTotal = majTot;
                cell.MinorTotal = minTot;
                cell.ReworkTotal = rewTot;
                db.QcDefects.Add(cell);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // --- floor alerts -----------------------------------------------------
    private static async Task SeedFloorAlertsAsync(FactoryBrainDbContext db, CancellationToken ct)
    {
        if (await db.FloorAlerts.AnyAsync(ct)) return;
        var rng = new Random(0xF100D);
        var tpls = new (FloorAlertSeverity Sev, string En, string Bn)[]
        {
            (FloorAlertSeverity.Warn,
             "Line {line} efficiency is {pct}% below target — review before the 10:00 standup.",
             "লাইন {line} এর দক্ষতা লক্ষ্যের চেয়ে {pct}% কম — ১০:০০ স্ট্যান্ডআপের আগে দেখুন।"),
            (FloorAlertSeverity.Critical,
             "Approval #{id} needs your sign-off — bearing replacement on {entity}.",
             "অনুমোদন #{id} আপনার স্বাক্ষরের অপেক্ষায় — {entity} এ বিয়ারিং প্রতিস্থাপন।"),
            (FloorAlertSeverity.Info,
             "Insight \"{title}\" was raised — open the brain feed for the full breakdown.",
             "\"{title}\" অন্তর্দৃষ্টি উত্থাপিত হয়েছে — বিস্তারিত জানতে ব্রেইন ফিড খুলুন।")
        };
        var lineLabels = new[] { "3", "5", "7" };
        var insightTitles = new[]
        {
            ("Two machines trending toward bearing wear", "দুটি মেশিন বিয়ারিং ক্ষয়ের দিকে এগোচ্ছে"),
            ("Line 3 slip — PO-4471 at risk", "লাইন ৩ বিলম্ব — PO-৪৪৭১ ঝুঁকিতে")
        };
        for (int i = 0; i < 6; i++)
        {
            var tpl = tpls[rng.Next(tpls.Length)];
            string en = tpl.En, bn = tpl.Bn;
            string? approvalId = null, insightId = null;
            if (tpl.Sev == FloorAlertSeverity.Critical)
            {
                approvalId = $"apr-{rng.Next(1000, 9999)}";
                en = en.Replace("{id}", approvalId[4..]).Replace("{entity}", $"M-{rng.Next(101, 310)}");
                bn = bn.Replace("{id}", approvalId[4..]).Replace("{entity}", $"M-{rng.Next(101, 310)}");
            }
            else if (tpl.Sev == FloorAlertSeverity.Info)
            {
                insightId = $"ins-{rng.Next(100, 999)}";
                var title = insightTitles[rng.Next(insightTitles.Length)];
                en = en.Replace("{title}", title.Item1);
                bn = bn.Replace("{title}", title.Item2);
            }
            else
            {
                en = en.Replace("{line}", lineLabels[rng.Next(lineLabels.Length)])
                        .Replace("{pct}", rng.Next(8, 22).ToString());
                bn = bn.Replace("{line}", lineLabels[rng.Next(lineLabels.Length)])
                        .Replace("{pct}", rng.Next(8, 22).ToString());
            }
            db.FloorAlerts.Add(new FloorAlert
            {
                Id = $"alert-{i + 1}",
                Channel = "whatsapp_sim",
                ApprovalId = approvalId,
                InsightId = insightId,
                BodyEn = en,
                BodyBn = bn,
                Severity = tpl.Sev,
                CreatedAt = DateTime.UtcNow.AddMinutes(-rng.Next(5, 240)),
                Read = rng.NextDouble() < 0.35
            });
        }
        await db.SaveChangesAsync(ct);
    }

    // --- orders / inventory placeholder -----------------------------------
    private static async Task SeedOrdersAndInventoryAsync(FactoryBrainDbContext db, CancellationToken ct)
    {
        if (await db.Products.AnyAsync(ct)) return;

        // Products (12 SKU seed)
        var products = new ProductRecord[]
        {
            new() { Id = "p-1", Sku = "APP-0042", Name = "Coral Apparel Pro", Category = "Apparel",  PriceBdt = 1899, CostBdt = 950, LeadTimeDays = 5 },
            new() { Id = "p-2", Sku = "APP-0103", Name = "Heritage Polo Tee",  Category = "Apparel",  PriceBdt = 1290, CostBdt = 640, LeadTimeDays = 5 },
            new() { Id = "p-3", Sku = "APP-0204", Name = "Dhaka Crew Neck",   Category = "Apparel",  PriceBdt = 1450, CostBdt = 720, LeadTimeDays = 5 },
            new() { Id = "p-4", Sku = "GRO-0117", Name = "Harvest Grocery Max", Category = "Grocery", PriceBdt = 720, CostBdt = 410, LeadTimeDays = 2 },
            new() { Id = "p-5", Sku = "BEA-0301", Name = "Sylhet Glow Serum",   Category = "Beauty",  PriceBdt = 2150, CostBdt = 980, LeadTimeDays = 3 },
            new() { Id = "p-6", Sku = "HOM-0411", Name = "Bengal Bamboo Mug",   Category = "Home",    PriceBdt = 650,  CostBdt = 260, LeadTimeDays = 7 },
        };
        db.Products.AddRange(products);

        // Customers (Bangladesh regions)
        db.Customers.AddRange(
            new CustomerRecord { Id = "c-1", Name = "Chowdhury Fashions",   Region = "Dhaka",      TotalOrders = 28, LtvBdt = 142_000, ChurnRisk = 0.12, LastOrderDays = 4 },
            new CustomerRecord { Id = "c-2", Name = "Latif Traders",        Region = "Chittagong", TotalOrders = 41, LtvBdt = 218_500, ChurnRisk = 0.31, LastOrderDays = 21 },
            new CustomerRecord { Id = "c-3", Name = "Sylhet Tea Co-op",     Region = "Sylhet",     TotalOrders = 19, LtvBdt =  88_900, ChurnRisk = 0.18, LastOrderDays = 9 },
            new CustomerRecord { Id = "c-4", Name = "Khulna Bricks",        Region = "Khulna",     TotalOrders =  7, LtvBdt =  39_400, ChurnRisk = 0.78, LastOrderDays = 78 },
            new CustomerRecord { Id = "c-5", Name = "Rajshahi Handicrafts", Region = "Rajshahi",   TotalOrders = 14, LtvBdt =  66_200, ChurnRisk = 0.55, LastOrderDays = 36 });

        // Suppliers
        db.Suppliers.AddRange(
            new SupplierRecord { Id = "s-1", Name = "Narayanganj Textiles", Region = "Dhaka",      LeadTimeDays = 7, OnTimeRate = 0.93 },
            new SupplierRecord { Id = "s-2", Name = "Sylhet Tea & Beauty",  Region = "Sylhet",     LeadTimeDays = 4, OnTimeRate = 0.88 },
            new SupplierRecord { Id = "s-3", Name = "Khulna Polymer Mills", Region = "Khulna",     LeadTimeDays = 9, OnTimeRate = 0.71 });

        // Inventory
        foreach (var p in products)
        {
            int stock = p.Sku is "APP-0042" or "GRO-0117" ? 40 : 600;
            double daily = p.Category == "Apparel" ? 8.5 : 2.1;
            int daysUntilStockout = Math.Max(0, (int)(stock / daily) - p.LeadTimeDays);
            db.Inventory.Add(new InventoryRecord
            {
                Id = $"inv-{p.Id}",
                ProductId = p.Id,
                Stock = stock,
                ReorderPoint = (int)Math.Round(daily * 14),
                ReorderQty = (int)Math.Round(daily * 30),
                DaysUntilStockout = daysUntilStockout,
                RecentDailyDemand = daily,
                AtRisk = daysUntilStockout < 14
            });
        }

        // Orders (sample rows — heavy dataset; full 14k rows generated lazily)
        var rng = new Random(unchecked((int)0xF00D0001));
        string[] channels = { "shopify", "whatsapp", "facebook", "instagram", "direct" };
        for (int i = 0; i < 600; i++)
        {
            var p = products[rng.Next(products.Length)];
            int qty = rng.Next(1, 5);
            db.Orders.Add(new OrderRecord
            {
                Id = $"o-{(i + 1).ToString("D6")}",
                CustomerId = $"c-{rng.Next(1, 6)}",
                ProductId = p.Id,
                Quantity = qty,
                TotalBdt = p.PriceBdt * qty,
                Region = rng.NextDouble() > 0.6 ? "Dhaka" :
                         rng.NextDouble() > 0.5 ? "Chittagong" :
                         rng.NextDouble() > 0.5 ? "Sylhet" :
                         rng.NextDouble() > 0.5 ? "Khulna" : "Rajshahi",
                Channel = channels[rng.Next(channels.Length)],
                DaysAgo = rng.Next(0, 360)
            });
        }
        await db.SaveChangesAsync(ct);
    }

    // --- admin user (idempotent, env-gated) ------------------------------
    /// <summary>
    /// If both <c>SEED_ADMIN_EMAIL</c> and <c>SEED_ADMIN_PASSWORD</c> are
    /// set, create an <see cref="User"/> with <c>Role = "Admin"</c> and a
    /// freshly-hashed password — unless a user with that email already
    /// exists (so the bootstrap is safe to run on every restart). The
    /// password is hashed via <see cref="IPasswordHasher"/>; the raw
    /// value never appears in logs, error messages, or DB columns.
    /// </summary>
    private static async Task SeedAdminUserAsync(
        FactoryBrainDbContext db,
        IPasswordHasher hasher,
        ILogger log,
        CancellationToken ct)
    {
        var email    = (Environment.GetEnvironmentVariable("SEED_ADMIN_EMAIL")    ?? string.Empty).Trim();
        var password = (Environment.GetEnvironmentVariable("SEED_ADMIN_PASSWORD") ?? string.Empty);
        if (email.Length == 0 || password.Length == 0) return; // skip silently

        var normalised = email.ToLowerInvariant();
        var existing = await db.Users.AnyAsync(u => u.Email == normalised, ct);
        if (existing) return;

        // Hash against a transient User instance; the hasher only reads
        // the User reference as a salt-pinning hint and the instance is
        // discarded after the call.
        var hashSeed = new User { Email = normalised };
        var hash = hasher.Hash(hashSeed, password);
        db.Users.Add(new User
        {
            Id            = Guid.NewGuid(),
            Email         = normalised,
            PasswordHash  = hash,
            Role          = "Admin",
            CreatedAt     = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        // Note: never log the password — only the email + the fact we
        // created the row.
        log.LogInformation("Seeded admin user {Email} from SEED_ADMIN_EMAIL.", normalised);
    }

    // --- viewer user (idempotent, env-gated) -----------------------------
    /// <summary>
    /// Same shape as <see cref="SeedAdminUserAsync"/> but for the Viewer
    /// role. Reads <c>SEED_VIEWER_EMAIL</c> + <c>SEED_VIEWER_PASSWORD</c>;
    /// if either is unset the seeder is a no-op. Used by the batch-52
    /// verification matrix to exercise the AdminOrLegacyToken policy
    /// against a non-Admin JWT (which the spec mandates returns 403
    /// problem details instead of the AdminToken envelope).
    /// </summary>
    private static async Task SeedViewerUserAsync(
        FactoryBrainDbContext db,
        IPasswordHasher hasher,
        ILogger log,
        CancellationToken ct)
    {
        var email    = (Environment.GetEnvironmentVariable("SEED_VIEWER_EMAIL")    ?? string.Empty).Trim();
        var password = (Environment.GetEnvironmentVariable("SEED_VIEWER_PASSWORD") ?? string.Empty);
        if (email.Length == 0 || password.Length == 0) return; // skip silently

        var normalised = email.ToLowerInvariant();
        var existing = await db.Users.AnyAsync(u => u.Email == normalised, ct);
        if (existing) return;

        var hashSeed = new User { Email = normalised };
        var hash = hasher.Hash(hashSeed, password);
        db.Users.Add(new User
        {
            Id            = Guid.NewGuid(),
            Email         = normalised,
            PasswordHash  = hash,
            Role          = "Viewer",
            CreatedAt     = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Seeded viewer user {Email} from SEED_VIEWER_EMAIL.", normalised);
    }

    // --- helpers ----------------------------------------------------------
    private static string Pick(Random r, params string[] xs) => xs[r.Next(xs.Length)];

    private static string[] RecentWeekStarts(int count)
    {
        var out0 = new List<string>();
        var d = DateTime.UtcNow;
        int offsetToMonday = ((int)d.DayOfWeek + 6) % 7;
        d = d.AddDays(-offsetToMonday);
        for (int i = 0; i < count; i++)
        {
            out0.Add(d.ToString("yyyy-MM-dd"));
            d = d.AddDays(-7);
        }
        return out0.ToArray();
    }
}
