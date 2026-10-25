using System.Buffers.Binary;

namespace FactoryBrain.Application.Qc;

public sealed record QcImageInfo(string Format, string ContentType, string Extension, int Width, int Height);

/// <summary>
/// Identifies an upload by its bytes, never by the client's file name or
/// Content-Type: JPEG, PNG and WebP are accepted, and their width / height
/// are read from the header (the image is not decoded). Anything else, a
/// truncated header, zero or oversized dimensions, or a file over the size
/// limit is rejected with a stable error code.
/// </summary>
public static class QcImageInspector
{
    public const string ErrEmpty = "image_empty";
    public const string ErrTooLarge = "image_too_large";
    public const string ErrUnsupported = "image_unsupported_format";
    public const string ErrCorrupt = "image_corrupt";
    public const string ErrDimensions = "image_dimensions_out_of_range";

    public static readonly IReadOnlyList<string> AcceptedContentTypes = new[] { "image/jpeg", "image/png", "image/webp" };

    public static bool TryInspect(ReadOnlySpan<byte> data, long maxBytes, out QcImageInfo? info, out string? error)
    {
        info = null;
        error = null;
        if (data.Length == 0) { error = ErrEmpty; return false; }
        if (data.Length > maxBytes) { error = ErrTooLarge; return false; }

        (string Format, string Type, string Ext, int W, int H)? parsed;
        if (IsPng(data)) parsed = ReadPng(data);
        else if (IsJpeg(data)) parsed = ReadJpeg(data);
        else if (IsWebp(data)) parsed = ReadWebp(data);
        else { error = ErrUnsupported; return false; }

        if (parsed is null) { error = ErrCorrupt; return false; }
        var p = parsed.Value;
        if (p.W <= 0 || p.H <= 0 || p.W > QcLimits.MaxImageDimension || p.H > QcLimits.MaxImageDimension)
        {
            error = ErrDimensions;
            return false;
        }
        info = new QcImageInfo(p.Format, p.Type, p.Ext, p.W, p.H);
        return true;
    }

    public static string Describe(string error) => error switch
    {
        ErrEmpty => "The image file is empty.",
        ErrTooLarge => "The image is larger than the upload limit.",
        ErrUnsupported => "Only JPEG, PNG and WebP images are accepted.",
        ErrCorrupt => "The image header could not be read; the file may be truncated or not a real image.",
        ErrDimensions => $"Image width and height must be between 1 and {QcLimits.MaxImageDimension} pixels.",
        _ => "The image was not accepted.",
    };

    private static bool IsPng(ReadOnlySpan<byte> d) =>
        d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47
        && d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A;

    private static bool IsJpeg(ReadOnlySpan<byte> d) => d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF;

    private static bool IsWebp(ReadOnlySpan<byte> d) =>
        d.Length >= 12 && d[0] == (byte)'R' && d[1] == (byte)'I' && d[2] == (byte)'F' && d[3] == (byte)'F'
        && d[8] == (byte)'W' && d[9] == (byte)'E' && d[10] == (byte)'B' && d[11] == (byte)'P';

    private static (string, string, string, int, int)? ReadPng(ReadOnlySpan<byte> d)
    {
        // Signature (8) + IHDR length (4) + "IHDR" (4) + width (4) + height (4).
        if (d.Length < 24) return null;
        if (d[12] != (byte)'I' || d[13] != (byte)'H' || d[14] != (byte)'D' || d[15] != (byte)'R') return null;
        var w = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(16, 4));
        var h = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(20, 4));
        if (w > int.MaxValue || h > int.MaxValue) return ("png", "image/png", "png", -1, -1);
        return ("png", "image/png", "png", (int)w, (int)h);
    }

    private static (string, string, string, int, int)? ReadJpeg(ReadOnlySpan<byte> d)
    {
        var i = 2;
        while (i + 3 < d.Length)
        {
            if (d[i] != 0xFF) return null;
            var marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }                         // fill byte
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; } // no length
            if (marker == 0xD9 || marker == 0xDA) return null;              // EOI / SOS before any frame header
            var len = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 2, 2));
            if (len < 2) return null;
            var isSof = marker is >= 0xC0 and <= 0xCF && marker is not 0xC4 and not 0xC8 and not 0xCC;
            if (isSof)
            {
                // length (2) + precision (1) + height (2) + width (2)
                if (i + 9 > d.Length || len < 7) return null;
                var h = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 5, 2));
                var w = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 7, 2));
                return ("jpeg", "image/jpeg", "jpg", w, h);
            }
            i += 2 + len;
        }
        return null;
    }

    private static (string, string, string, int, int)? ReadWebp(ReadOnlySpan<byte> d)
    {
        if (d.Length < 30) return null;
        var fourcc = d.Slice(12, 4);
        if (fourcc.SequenceEqual("VP8 "u8))
        {
            // Frame tag (3) then the key-frame start code 9D 01 2A, then 14-bit width / height.
            if (d[23] != 0x9D || d[24] != 0x01 || d[25] != 0x2A) return null;
            var w = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(26, 2)) & 0x3FFF;
            var h = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(28, 2)) & 0x3FFF;
            return ("webp", "image/webp", "webp", w, h);
        }
        if (fourcc.SequenceEqual("VP8L"u8))
        {
            if (d[20] != 0x2F) return null;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(21, 4));
            var w = (int)(bits & 0x3FFF) + 1;
            var h = (int)((bits >> 14) & 0x3FFF) + 1;
            return ("webp", "image/webp", "webp", w, h);
        }
        if (fourcc.SequenceEqual("VP8X"u8))
        {
            var w = (d[24] | d[25] << 8 | d[26] << 16) + 1;
            var h = (d[27] | d[28] << 8 | d[29] << 16) + 1;
            return ("webp", "image/webp", "webp", w, h);
        }
        return null;
    }
}
