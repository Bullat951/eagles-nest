using System.IO.Compression;

/// <summary>Minimal PNG writer (8-bit RGB) for the probe's debug pictures.</summary>
static class Png
{
    public static byte[] Rgb(int width, int height, byte[] rgb)
    {
        var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            for (int y = 0; y < height; y++)
            {
                z.WriteByte(0);  // filter: none
                z.Write(rgb, y * width * 3, width * 3);
            }
        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, (uint)width);
        BigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // RGB
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        BigEndian(head, 0, (uint)data.Length);
        for (int k = 0; k < 4; k++) head[4 + k] = (byte)type[k];
        s.Write(head);
        s.Write(data);
        var crc = Crc(head.AsSpan(4), data);
        var tail = new byte[4];
        BigEndian(tail, 0, crc);
        s.Write(tail);
    }

    private static uint Crc(ReadOnlySpan<byte> type, byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type) c = Step(c, b);
        foreach (var b in data) c = Step(c, b);
        return c ^ 0xFFFFFFFFu;

        static uint Step(uint c, byte b)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }
    }

    private static void BigEndian(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24);
        b[o + 1] = (byte)(v >> 16);
        b[o + 2] = (byte)(v >> 8);
        b[o + 3] = (byte)v;
    }
}
