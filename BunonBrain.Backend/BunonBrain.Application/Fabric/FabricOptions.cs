using System.Globalization;

namespace BunonBrain.Application.Fabric;

/// <summary>
/// Fabric-inspection rules, read once from the environment at startup. The
/// defaults are a common 4-point acceptance; set them to the plant's /
/// buyers' standard.
/// <list type="bullet">
///   <item><c>FABRIC_POINTS_PASS_MAX</c> (default 40): highest points per 100 square yards for a roll to pass.</item>
///   <item><c>FABRIC_POINTS_REJECT_ABOVE</c> (default 60): above this a roll is recommended for rejection;
///   between the two it is restricted (usable only with an approved override).</item>
///   <item><c>FABRIC_WIDTH_TOLERANCE_CM</c> (default 2): a measured cuttable width this much below the ticket width restricts the roll.</item>
///   <item><c>FABRIC_MAX_REJECTED_PCT</c> (default 10): a roll whose cut-out (rejected) length is a larger share is restricted.</item>
/// </list>
/// The vision provider, its timeout and confidence threshold, and the image
/// store and size limit are those of quality inspection (<c>QC_*</c>).
/// Separation of duties for overrides and cutting approvals follows
/// <c>PRODUCTION_REQUIRE_SEPARATE_APPROVER</c>.
/// </summary>
public sealed record FabricOptions(
    double PassMaxPointsPer100SqYd,
    double RejectAbovePointsPer100SqYd,
    double WidthToleranceCm,
    double MaxRejectedPct,
    string? ConfigurationError)
{
    public static FabricOptions Default { get; } = FromEnvironment(_ => null);

    public static FabricOptions FromEnvironment(Func<string, string?> env)
    {
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var errors = new List<string>();

        double Num(string name, double def, double min, double max)
        {
            var raw = Clean(env(name));
            if (raw is null) return def;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max) return v;
            errors.Add($"{name}='{raw}' must be a number from {min} to {max}; using {def}.");
            return def;
        }

        var pass = Num("FABRIC_POINTS_PASS_MAX", 40, 0, 1000);
        var reject = Num("FABRIC_POINTS_REJECT_ABOVE", 60, 0, 1000);
        if (reject < pass)
        {
            errors.Add($"FABRIC_POINTS_REJECT_ABOVE ({reject}) is below FABRIC_POINTS_PASS_MAX ({pass}); using {pass} for both (no restricted band).");
            reject = pass;
        }
        return new FabricOptions(pass, reject,
            Num("FABRIC_WIDTH_TOLERANCE_CM", 2, 0, 50),
            Num("FABRIC_MAX_REJECTED_PCT", 10, 0, 100),
            errors.Count == 0 ? null : string.Join(" ", errors));
    }
}
