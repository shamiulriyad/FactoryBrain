using BunonBrain.Application.Abstractions.Interfaces;

namespace BunonBrain.Application.Qc;

/// <summary>
/// Multi-defect coverage for vision models. A general vision-language model
/// sees a downscaled photo and tends to report the most obvious defect only,
/// so the image is also inspected as an overlapping grid of zoomed crops
/// (tiles). Each tile's boxes are mapped back to whole-image coordinates and
/// the results are merged, dropping duplicates of the same defect found in
/// several passes. All coordinates are normalised to [0,1], origin top-left.
/// </summary>
public static class QcTiling
{
    /// <summary>Fraction of a cell added on each inner side so defects on a cell border are seen whole.</summary>
    public const double DefaultOverlap = 0.15;

    /// <summary>Boxes overlapping at least this much (IoU) are the same defect, whatever their labels.</summary>
    public const double DuplicateIou = 0.3;

    /// <summary>With the same label, a lower IoU (or one box containing the other's centre) is enough.</summary>
    public const double SameLabelIou = 0.1;

    /// <summary>
    /// The tiles of a <paramref name="grid"/>×<paramref name="grid"/> layout,
    /// row by row. Grid 1 (or less) means no tiling and returns no tiles.
    /// </summary>
    public static IReadOnlyList<NormalizedBox> Tiles(int grid, double overlap = DefaultOverlap)
    {
        if (grid <= 1) return Array.Empty<NormalizedBox>();
        var step = 1.0 / grid;
        var pad = step * overlap;
        var tiles = new List<NormalizedBox>(grid * grid);
        for (var row = 0; row < grid; row++)
        for (var col = 0; col < grid; col++)
        {
            var x0 = Math.Max(0, col * step - pad);
            var y0 = Math.Max(0, row * step - pad);
            var x1 = Math.Min(1, (col + 1) * step + pad);
            var y1 = Math.Min(1, (row + 1) * step + pad);
            tiles.Add(new NormalizedBox(x0, y0, x1 - x0, y1 - y0));
        }
        return tiles;
    }

    /// <summary>A human-readable name for a tile, used in the prompt ("top-left part").</summary>
    public static string Describe(int index, int grid)
    {
        var row = index / grid;
        var col = index % grid;
        string Pos(int i, string first, string middle, string last) => i == 0 ? first : i == grid - 1 ? last : middle;
        var v = Pos(row, "top", "middle", "bottom");
        var h = Pos(col, "left", "centre", "right");
        return v == "middle" && h == "centre" ? "centre" : $"{v}-{h}";
    }

    /// <summary>Map a defect found in <paramref name="tile"/> (box relative to the tile) to whole-image coordinates.</summary>
    public static DetectedDefect ToWholeImage(DetectedDefect d, NormalizedBox tile)
    {
        if (d.Box is not { } b) return d;
        return d with
        {
            Box = new NormalizedBox(tile.X + b.X * tile.Width, tile.Y + b.Y * tile.Height, b.Width * tile.Width, b.Height * tile.Height),
            Polygon = null, // the vision model never returns polygons; don't carry tile-relative points over
        };
    }

    /// <summary>
    /// Merge the whole-image pass with the tile passes (tile boxes already in
    /// whole-image coordinates). Located defects are de-duplicated greedily by
    /// confidence; a duplicate's explanation fills in a kept defect that has
    /// none. Tile defects without a box are dropped (they can't be placed and
    /// the whole-image pass covers unlocated defects). An unlocated
    /// whole-image defect is kept only when no located defect has its label.
    /// </summary>
    public static IReadOnlyList<DetectedDefect> Merge(IReadOnlyList<DetectedDefect> whole, IEnumerable<DetectedDefect> tiles)
    {
        var located = whole.Where(d => d.Box is not null)
            .Concat(tiles.Where(d => d.Box is not null))
            .OrderByDescending(d => d.Confidence ?? 0)
            .ToList();

        var kept = new List<DetectedDefect>();
        foreach (var d in located)
        {
            var dup = kept.FindIndex(k => IsDuplicate(k, d));
            if (dup < 0) { kept.Add(d); continue; }
            if (kept[dup].Insight is null && d.Insight is not null) kept[dup] = kept[dup] with { Insight = d.Insight };
        }

        foreach (var d in whole.Where(d => d.Box is null))
        {
            if (!kept.Any(k => SameLabel(k, d))) kept.Add(d);
        }
        return kept;
    }

    public static bool IsDuplicate(DetectedDefect a, DetectedDefect b)
    {
        if (a.Box is not { } x || b.Box is not { } y) return false;
        var iou = Iou(x, y);
        if (iou >= DuplicateIou) return true;
        return SameLabel(a, b) && (iou >= SameLabelIou || Contains(x, Centre(y)) || Contains(y, Centre(x)));
    }

    public static double Iou(NormalizedBox a, NormalizedBox b)
    {
        var ix = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X));
        var iy = Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        var inter = ix * iy;
        var union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }

    private static bool SameLabel(DetectedDefect a, DetectedDefect b) =>
        string.Equals(a.Label.Trim(), b.Label.Trim(), StringComparison.OrdinalIgnoreCase);

    private static (double X, double Y) Centre(NormalizedBox b) => (b.X + b.Width / 2, b.Y + b.Height / 2);

    private static bool Contains(NormalizedBox b, (double X, double Y) p) =>
        p.X >= b.X && p.X <= b.X + b.Width && p.Y >= b.Y && p.Y <= b.Y + b.Height;
}
