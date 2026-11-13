namespace BunonBrain.Application.Fabric;

/// <summary>A fabric defect category.</summary>
/// <param name="IsOpening">A hole / opening: scored by the 4-point rule for openings (≤ 1 in = 2 points, larger = 4).</param>
/// <param name="DefaultCutOut">New defects of this category are marked as cut out unless the inspector changes it.</param>
public sealed record FabricDefectCategory(string Code, string Name, string Description, string DefaultSeverity, bool IsOpening, bool DefaultCutOut);

/// <summary>
/// The fabric defect taxonomy: common woven and knit roll defects used in
/// 4-point inspection. It is reference data shipped with the code (not
/// plant records); <c>other</c> covers anything else, with a description.
/// Codes are stable: stored defects refer to them.
/// </summary>
public static class FabricTaxonomy
{
    public static readonly IReadOnlyList<FabricDefectCategory> Categories = new FabricDefectCategory[]
    {
        new("hole", "Hole / cut / tear", "Any opening through the fabric, including cuts, tears and burst places.", "critical", true, true),
        new("slub", "Slub / thick place", "Thick, uneven section of yarn woven or knitted in.", "minor", false, false),
        new("thin_place", "Thin place", "Section of noticeably thinner yarn or lower density.", "minor", false, false),
        new("broken_end", "Broken / missing end", "Warp yarn missing or broken over a length (woven).", "major", false, false),
        new("broken_pick", "Broken / missing pick", "Weft yarn missing or broken across the width (woven).", "major", false, false),
        new("float", "Float", "Yarn floating over threads it should interlace with (woven).", "major", false, false),
        new("knot", "Knot", "Visible knot or tied yarn end.", "minor", false, false),
        new("stain", "Stain / spot", "Oil, grease, rust, dirt or dye spot.", "major", false, false),
        new("shade_variation", "Shade variation", "Colour differs within the roll (side-to-side, end-to-end or patches).", "major", false, false),
        new("barre", "Barré / stripe", "Repeating horizontal bands from yarn or tension differences (knits).", "major", false, false),
        new("needle_line", "Needle line", "Vertical line from a faulty needle (knits).", "major", false, false),
        new("dropped_stitch", "Dropped stitch / ladder", "Missed loops forming a run (knits).", "critical", false, true),
        new("crease_mark", "Crease / crush mark", "Permanent fold, crease or pressure mark.", "minor", false, false),
        new("print_defect", "Print defect", "Misregistration, smudge, missing print or colour bleed.", "major", false, false),
        new("contamination", "Foreign fibre / contamination", "Foreign yarn, fly or fibre woven or knitted in.", "minor", false, false),
        new("selvedge_defect", "Selvedge defect", "Torn, curled, tight or loose selvedge.", "minor", false, false),
        new("bow_skew", "Bow / skew", "Weft or courses not square to the selvedge.", "major", false, false),
        new("other", "Other", "A defect none of the above describes; say what it is.", "major", false, false),
    };

    private static readonly Dictionary<string, FabricDefectCategory> ByCode = Categories.ToDictionary(c => c.Code, StringComparer.Ordinal);

    public static FabricDefectCategory? Find(string? code) => code is not null && ByCode.TryGetValue(code, out var c) ? c : null;

    public static bool IsKnown(string? code) => Find(code) is not null;
}
