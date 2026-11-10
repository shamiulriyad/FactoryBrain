namespace FactoryBrain.Application.Fabric;

/// <summary>A defect's position on the roll: metres along from the head end, centimetres across from the left selvedge.</summary>
public sealed record RollLocation(double? AlongStartM, double? AlongLengthCm, double? AcrossStartCm, double? AcrossWidthCm)
{
    public static readonly RollLocation None = new(null, null, null, null);

    /// <summary><c>mapped</c> (along and across), <c>along_only</c>, <c>unmapped</c>.</summary>
    public string State => AlongStartM is null ? "unmapped" : AcrossStartCm is null ? "along_only" : "mapped";

    /// <summary>Largest known extent, centimetres (the 4-point "defect size"); null when no extent is known.</summary>
    public double? SizeCm => (AlongLengthCm, AcrossWidthCm) switch
    {
        (null, null) => null,
        ({ } a, null) => a,
        (null, { } w) => w,
        ({ } a, { } w) => Math.Max(a, w),
    };
}

/// <summary>Image box, fractions [0,1] of the image width / height, origin top-left.</summary>
public sealed record ImageBox(double X, double Y, double Width, double Height);

/// <summary>
/// User-entered frame calibration: which region of the roll the image shows.
/// <para><c>length_down</c>: the roll's length runs down the image. <see cref="AlongStartM"/> is the
/// along position at the image's top edge and <see cref="AlongEndM"/> at its bottom edge;
/// <see cref="AcrossStartCm"/> is at the left edge and <see cref="AcrossEndCm"/> at the right edge.</para>
/// <para><c>length_right</c>: the length runs left → right. Along positions are at the left / right
/// edges, across positions at the top / bottom edges.</para>
/// Either pair may be given in decreasing order (a mirrored or reversed capture).
/// </summary>
public sealed record FrameCalibration(string Orientation, double AlongStartM, double AlongEndM, double AcrossStartCm, double AcrossEndCm);

/// <summary>
/// Validation of roll dimensions and defect positions, and the mapping from a
/// calibrated image to roll coordinates. Nothing here guesses a scale: an
/// image without a calibration maps to no physical position at all.
/// </summary>
public static class FabricGeometry
{
    public const double MaxRollLengthM = 5000;
    public const double MinRollWidthCm = 10;
    public const double MaxRollWidthCm = 400;
    /// <summary>A position may overshoot the roll end by rounding, this much (1 cm).</summary>
    public const double AlongToleranceM = 0.01;
    /// <summary>Across tolerance (0.5 cm).</summary>
    public const double AcrossToleranceCm = 0.5;
    /// <summary>A defect longer than this along the roll is recorded as several defects or a cut-out region (100 m).</summary>
    public const double MaxDefectAlongCm = 10_000;
    /// <summary>Image and frame aspect ratios further apart than this (relative) produce a calibration warning.</summary>
    public const double AspectWarningRatio = 0.15;

    public static readonly string[] Orientations = { "length_down", "length_right" };

    // ── Roll dimensions ─────────────────────────────────────────────────────

    public static string? CheckLength(double? metres, string what = "Length")
    {
        if (metres is not { } m || !FabricUnits.IsFinite(m)) return $"{what} is required.";
        if (m <= 0) return $"{what} must be more than 0.";
        if (m > MaxRollLengthM) return $"{what} must be at most {MaxRollLengthM:0} m.";
        return null;
    }

    public static string? CheckWidth(double? cm, string what = "Width")
    {
        if (cm is not { } w || !FabricUnits.IsFinite(w)) return $"{what} is required.";
        if (w < MinRollWidthCm || w > MaxRollWidthCm) return $"{what} must be from {MinRollWidthCm:0} to {MaxRollWidthCm:0} cm.";
        return null;
    }

    /// <summary>The length every roll calculation uses: measured when recorded, else the ticket length.</summary>
    public static double ReferenceLength(double ticketM, double? measuredM) => measuredM ?? ticketM;

    /// <summary>The cuttable width: measured (inside the selvedges) when recorded, else the ticket width.</summary>
    public static double CuttableWidth(double ticketCm, double? measuredCm) => measuredCm ?? ticketCm;

    /// <summary>The full width across which defect positions are validated (the larger of ticket and measured).</summary>
    public static double FullWidth(double ticketCm, double? measuredCm) => Math.Max(ticketCm, measuredCm ?? 0);

    // ── Defect positions ────────────────────────────────────────────────────

    /// <summary>
    /// Field errors for a defect position on a roll of <paramref name="rollLengthM"/> ×
    /// <paramref name="rollWidthCm"/>. Every part is optional, but an extent needs its start and an
    /// across position needs an along position.
    /// </summary>
    public static Dictionary<string, string> ValidateLocation(RollLocation l, double rollLengthM, double rollWidthCm)
    {
        var e = new Dictionary<string, string>();
        bool Bad(double? v) => v is { } x && !FabricUnits.IsFinite(x);
        if (Bad(l.AlongStartM) || Bad(l.AlongLengthCm) || Bad(l.AcrossStartCm) || Bad(l.AcrossWidthCm))
        {
            e["location"] = "Positions must be numbers.";
            return e;
        }

        if (l.AlongStartM is { } start)
        {
            if (start < 0) e["alongStartM"] = "The along position can't be negative (0 m is the head end).";
            else if (start > rollLengthM + AlongToleranceM)
                e["alongStartM"] = $"The along position {start:0.###} m is beyond the end of the roll ({rollLengthM:0.###} m).";
        }
        if (l.AlongLengthCm is { } len)
        {
            if (l.AlongStartM is null) e["alongLengthCm"] = "Give the along position of a defect whose length is known.";
            else if (len <= 0) e["alongLengthCm"] = "The defect length must be more than 0 cm.";
            else if (len > MaxDefectAlongCm) e["alongLengthCm"] = $"A defect is at most {MaxDefectAlongCm / 100:0} m long; record longer faults as several defects.";
            else if (l.AlongStartM is { } s && !e.ContainsKey("alongStartM") && s + len / 100 > rollLengthM + AlongToleranceM)
                e["alongLengthCm"] = $"The defect runs past the end of the roll ({s:0.###} m + {len:0.#} cm > {rollLengthM:0.###} m).";
        }
        if (l.AcrossStartCm is { } across)
        {
            if (l.AlongStartM is null) e["acrossStartCm"] = "An across position needs an along position.";
            else if (across < 0) e["acrossStartCm"] = "The across position can't be negative (0 cm is the left selvedge).";
            else if (across > rollWidthCm + AcrossToleranceCm)
                e["acrossStartCm"] = $"The across position {across:0.#} cm is outside the roll width ({rollWidthCm:0.#} cm).";
        }
        if (l.AcrossWidthCm is { } w)
        {
            if (w <= 0) e["acrossWidthCm"] = "The defect width must be more than 0 cm.";
            else if (w > rollWidthCm + AcrossToleranceCm) e["acrossWidthCm"] = $"The defect is wider than the roll ({rollWidthCm:0.#} cm).";
            else if (l.AcrossStartCm is { } a && !e.ContainsKey("acrossStartCm") && a + w > rollWidthCm + AcrossToleranceCm)
                e["acrossWidthCm"] = $"The defect extends past the right selvedge ({a:0.#} + {w:0.#} cm > {rollWidthCm:0.#} cm).";
        }
        return e;
    }

    // ── Calibration ─────────────────────────────────────────────────────────

    /// <summary>Errors (field → message) and warnings for a calibration of a <paramref name="widthPx"/> × <paramref name="heightPx"/> image.</summary>
    public static (Dictionary<string, string> Errors, List<string> Warnings) ValidateCalibration(
        FrameCalibration c, double rollLengthM, double rollWidthCm, int widthPx, int heightPx)
    {
        var e = new Dictionary<string, string>();
        var warnings = new List<string>();
        if (!Orientations.Contains(c.Orientation))
            e["orientation"] = $"orientation must be one of: {string.Join(", ", Orientations)}.";
        double[] all = { c.AlongStartM, c.AlongEndM, c.AcrossStartCm, c.AcrossEndCm };
        if (all.Any(v => !FabricUnits.IsFinite(v)))
        {
            e["frame"] = "Frame positions must be numbers.";
            return (e, warnings);
        }
        foreach (var (v, key) in new[] { (c.AlongStartM, "frameAlongStartM"), (c.AlongEndM, "frameAlongEndM") })
            if (v < 0 || v > rollLengthM + AlongToleranceM)
                e[key] = $"Along positions must be within the roll (0–{rollLengthM:0.###} m).";
        foreach (var (v, key) in new[] { (c.AcrossStartCm, "frameAcrossStartCm"), (c.AcrossEndCm, "frameAcrossEndCm") })
            if (v < 0 || v > rollWidthCm + AcrossToleranceCm)
                e[key] = $"Across positions must be within the roll width (0–{rollWidthCm:0.#} cm).";
        var alongSpanCm = Math.Abs(c.AlongEndM - c.AlongStartM) * 100;
        var acrossSpanCm = Math.Abs(c.AcrossEndCm - c.AcrossStartCm);
        if (alongSpanCm < 0.5 && !e.ContainsKey("frameAlongEndM")) e["frameAlongEndM"] = "The frame must cover some length of the roll (start and end differ).";
        if (acrossSpanCm < 0.5 && !e.ContainsKey("frameAcrossEndCm")) e["frameAcrossEndCm"] = "The frame must cover some width of the roll (start and end differ).";
        if (e.Count > 0 || widthPx <= 0 || heightPx <= 0) return (e, warnings);

        // Physical aspect (along ÷ across) against the pixel aspect along the same axes.
        var pixelAlong = c.Orientation == "length_down" ? heightPx : widthPx;
        var pixelAcross = c.Orientation == "length_down" ? widthPx : heightPx;
        var physical = alongSpanCm / acrossSpanCm;
        var pixels = pixelAlong / (double)pixelAcross;
        var mismatch = Math.Abs(physical / pixels - 1);
        if (mismatch > AspectWarningRatio)
            warnings.Add($"The frame is {alongSpanCm:0.#} × {acrossSpanCm:0.#} cm (along × across) but the image is {pixelAlong} × {pixelAcross} px: " +
                         $"the proportions differ by {mismatch * 100:0} %. Mapped positions are stretched unless the photo was taken square-on and the frame values are right.");
        return (e, warnings);
    }

    /// <summary>
    /// Map an image box to roll coordinates through a validated calibration
    /// (a linear map: the capture is assumed flat and square-on to the
    /// fabric; perspective and lens distortion are not corrected).
    /// </summary>
    public static RollLocation MapBox(ImageBox box, FrameCalibration c)
    {
        var (alongFrac0, alongFrac1, acrossFrac0, acrossFrac1) = c.Orientation == "length_down"
            ? (box.Y, box.Y + box.Height, box.X, box.X + box.Width)
            : (box.X, box.X + box.Width, box.Y, box.Y + box.Height);
        double Lerp(double a, double b, double t) => a + (b - a) * t;
        var along0 = Lerp(c.AlongStartM, c.AlongEndM, alongFrac0);
        var along1 = Lerp(c.AlongStartM, c.AlongEndM, alongFrac1);
        var across0 = Lerp(c.AcrossStartCm, c.AcrossEndCm, acrossFrac0);
        var across1 = Lerp(c.AcrossStartCm, c.AcrossEndCm, acrossFrac1);
        var alongStart = Math.Min(along0, along1);
        var acrossStart = Math.Min(across0, across1);
        return new RollLocation(
            FabricUnits.RoundMm(alongStart),
            Math.Max(0.1, FabricUnits.Round(Math.Abs(along1 - along0) * 100, 1)),
            FabricUnits.Round(acrossStart, 1),
            Math.Max(0.1, FabricUnits.Round(Math.Abs(across1 - across0), 1)));
    }

    /// <summary>Clip a mapped location to the roll (rounding at the edges); null parts stay null.</summary>
    public static RollLocation Clip(RollLocation l, double rollLengthM, double rollWidthCm)
    {
        double? alongStart = l.AlongStartM is { } s ? Math.Clamp(s, 0, rollLengthM) : null;
        double? alongLen = l.AlongLengthCm is { } len && alongStart is { } st ? Math.Max(0.1, Math.Min(len, (rollLengthM - st) * 100)) : l.AlongLengthCm;
        double? acrossStart = l.AcrossStartCm is { } a ? Math.Clamp(a, 0, rollWidthCm) : null;
        double? acrossW = l.AcrossWidthCm is { } w && acrossStart is { } ast ? Math.Max(0.1, Math.Min(w, rollWidthCm - ast)) : l.AcrossWidthCm;
        return new RollLocation(alongStart, alongLen is { } x ? FabricUnits.Round(x, 1) : null, acrossStart, acrossW is { } y ? FabricUnits.Round(y, 1) : null);
    }
}
