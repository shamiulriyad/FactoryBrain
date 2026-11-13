namespace BunonBrain.Application.Fabric;

/// <summary>
/// Unit conversion for fabric dimensions. Canonical units are metres along
/// the roll and centimetres across it; inputs may be given in yards / inches
/// (exact international definitions: 1 yd = 0.9144 m, 1 in = 2.54 cm).
/// Values are rounded to millimetre precision on the way in, so repeated
/// conversions never accumulate floating-point noise.
/// </summary>
public static class FabricUnits
{
    public const double MetresPerYard = 0.9144;
    public const double CmPerInch = 2.54;

    public static readonly string[] LengthUnits = { "m", "yd", "cm" };
    public static readonly string[] WidthUnits = { "cm", "in", "mm", "m" };

    /// <summary>A length along the roll in <paramref name="unit"/> → metres; null for an unknown unit.</summary>
    public static double? ToMetres(double value, string? unit) => (unit ?? "m").Trim().ToLowerInvariant() switch
    {
        "m" or "metre" or "meter" or "metres" or "meters" => RoundMm(value),
        "yd" or "yard" or "yards" => RoundMm(value * MetresPerYard),
        "cm" => RoundMm(value / 100),
        _ => null,
    };

    /// <summary>A width across the roll in <paramref name="unit"/> → centimetres; null for an unknown unit.</summary>
    public static double? ToCentimetres(double value, string? unit) => (unit ?? "cm").Trim().ToLowerInvariant() switch
    {
        "cm" => RoundTenthMm(value),
        "in" or "inch" or "inches" => RoundTenthMm(value * CmPerInch),
        "mm" => RoundTenthMm(value / 10),
        "m" => RoundTenthMm(value * 100),
        _ => null,
    };

    public static string? NormalizeLengthUnit(string? unit) => (unit ?? "m").Trim().ToLowerInvariant() switch
    {
        "m" or "metre" or "meter" or "metres" or "meters" => "m",
        "yd" or "yard" or "yards" => "yd",
        "cm" => "cm",
        _ => null,
    };

    public static string? NormalizeWidthUnit(string? unit) => (unit ?? "cm").Trim().ToLowerInvariant() switch
    {
        "cm" => "cm",
        "in" or "inch" or "inches" => "in",
        "mm" => "mm",
        "m" => "m",
        _ => null,
    };

    public static double MetresToYards(double m) => m / MetresPerYard;
    public static double CmToInches(double cm) => cm / CmPerInch;

    /// <summary>Area of a length (m) × width (cm) strip, square metres.</summary>
    public static double AreaM2(double lengthM, double widthCm) => lengthM * widthCm / 100.0;

    /// <summary>Square metres → square yards.</summary>
    public static double SquareMetresToSquareYards(double m2) => m2 / (MetresPerYard * MetresPerYard);

    /// <summary>Metres, to the millimetre.</summary>
    public static double RoundMm(double metres) => Math.Round(metres, 3, MidpointRounding.AwayFromZero);

    /// <summary>Centimetres, to the tenth of a millimetre.</summary>
    public static double RoundTenthMm(double cm) => Math.Round(cm, 2, MidpointRounding.AwayFromZero);

    /// <summary>Rounded for display / reporting (half away from zero).</summary>
    public static double Round(double v, int digits) => Math.Round(v, digits, MidpointRounding.AwayFromZero);

    public static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}
