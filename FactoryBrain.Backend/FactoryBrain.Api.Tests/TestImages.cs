using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FactoryBrain.Api.Tests;

/// <summary>Image bytes for the QC tests: real, decodable PNGs and header-only JPEG / WebP samples.</summary>
public static class TestImages
{
    /// <summary>A complete, valid RGB PNG of the given size filled with one colour.</summary>
    public static byte[] Png(int width, int height, byte r = 200, byte g = 180, byte b = 160)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0); // filter: none
            for (var x = 0; x < width; x++) { raw.WriteByte(r); raw.WriteByte(g); raw.WriteByte(b); }
        }
        byte[] idat;
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true)) raw.WriteTo(zs);
            idat = z.ToArray();
        }

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // colour type: RGB
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", idat);
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    /// <summary>A PNG header declaring the given size (enough for header-level checks).</summary>
    public static byte[] PngHeader(uint width, uint height)
    {
        var d = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(d, 0);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(d, 12);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(20), height);
        return d;
    }

    /// <summary>JPEG: SOI, an APP0 segment, then a baseline SOF0 frame header.</summary>
    public static byte[] JpegHeader(ushort width, ushort height)
    {
        using var m = new MemoryStream();
        m.Write(new byte[] { 0xFF, 0xD8 });
        m.Write(new byte[] { 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 });
        m.Write(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08 });
        m.Write(new[] { (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width });
        m.Write(new byte[] { 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1 });
        m.Write(new byte[] { 0xFF, 0xD9 });
        return m.ToArray();
    }

    /// <summary>WebP extended (VP8X) header with the given canvas size.</summary>
    public static byte[] WebpHeader(int width, int height)
    {
        var d = new byte[30];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(d, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), 22);
        Encoding.ASCII.GetBytes("WEBPVP8X").CopyTo(d, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(16), 10);
        var w = width - 1; var h = height - 1;
        d[24] = (byte)w; d[25] = (byte)(w >> 8); d[26] = (byte)(w >> 16);
        d[27] = (byte)h; d[28] = (byte)(h >> 8); d[29] = (byte)(h >> 16);
        return d;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc32(typeBytes.Concat(data).ToArray());
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
