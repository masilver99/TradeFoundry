using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ChatGptPlanSpike;

/// <summary>Dependency-free PNG generator for deterministic vision checks (colored vertical bands).</summary>
public static class TestImages
{
    public static readonly (string Name, byte R, byte G, byte B)[] RedGreenBlue = [("red", 220, 30, 30), ("green", 30, 170, 60), ("blue", 30, 80, 220)];
    public static readonly (string Name, byte R, byte G, byte B)[] YellowBlack = [("yellow", 250, 220, 0), ("black", 0, 0, 0)];

    public static byte[] Bands((string Name, byte R, byte G, byte B)[] bands, int width = 600, int height = 200)
    {
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 3);
            raw[row] = 0; // filter: none
            for (var x = 0; x < width; x++)
            {
                var band = bands[Math.Min(bands.Length - 1, x * bands.Length / width)];
                var p = row + 1 + x * 3;
                raw[p] = band.R; raw[p + 1] = band.G; raw[p + 2] = band.B;
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, Crc32(typeBytes, data));
        output.Write(buffer);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>True when every expected word appears in the text, in order.</summary>
    public static bool InOrder(string text, IEnumerable<string> words)
    {
        var index = 0;
        var lower = text.ToLowerInvariant();
        foreach (var word in words)
        {
            var found = lower.IndexOf(word, index, StringComparison.Ordinal);
            if (found < 0) return false;
            index = found + word.Length;
        }
        return true;
    }
}
