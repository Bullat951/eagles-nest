using System.IO;

namespace Dcb.Client.Map.Charts;

/// <summary>Minimal DDS reader for DCS chart tiles: DXT5 (BC3), plain 128-byte header, full mip chain.</summary>
public static class Dds
{
    private const int HeaderSize = 128;

    /// <summary>Decodes one mip level to BGRA32. Returns (pixels, width, height).</summary>
    public static (byte[] Bgra, int Width, int Height) DecodeDxt5(byte[] file, int mip)
    {
        if (file.Length < HeaderSize || file[0] != 'D' || file[1] != 'D' || file[2] != 'S' || file[3] != ' ')
            throw new InvalidDataException("not a DDS file");
        if (BitConverter.ToUInt32(file, 84) != 0x35545844)  // "DXT5"
            throw new InvalidDataException("only DXT5 is supported");
        int height = BitConverter.ToInt32(file, 12), width = BitConverter.ToInt32(file, 16);
        int mips = Math.Max(1, BitConverter.ToInt32(file, 28));
        mip = Math.Clamp(mip, 0, mips - 1);

        int offset = HeaderSize;
        for (int k = 0; k < mip; k++) offset += BlockBytes(width >> k, height >> k);
        int w = Math.Max(1, width >> mip), h = Math.Max(1, height >> mip);
        if (offset + BlockBytes(w, h) > file.Length) throw new InvalidDataException("truncated DDS");
        return (DecodeBc3(file, offset, w, h), w, h);
    }

    /// <summary>Decodes raw BC3 (DXT5) blocks of a w×h image starting at <paramref name="offset"/> to BGRA32.</summary>
    public static byte[] DecodeBc3(byte[] file, int offset, int w, int h)
    {
        var bgra = new byte[w * h * 4];
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        Span<byte> alpha = stackalloc byte[8];
        Span<byte> colors = stackalloc byte[16];  // 4 colours × BGRA
        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++, offset += 16)
            {
                // Alpha: two endpoints, 3-bit indices for 16 pixels.
                alpha[0] = file[offset];
                alpha[1] = file[offset + 1];
                if (alpha[0] > alpha[1])
                    for (int k = 1; k < 7; k++) alpha[k + 1] = (byte)(((7 - k) * alpha[0] + k * alpha[1]) / 7);
                else
                {
                    for (int k = 1; k < 5; k++) alpha[k + 1] = (byte)(((5 - k) * alpha[0] + k * alpha[1]) / 5);
                    alpha[6] = 0;
                    alpha[7] = 255;
                }
                ulong alphaBits = 0;
                for (int k = 0; k < 6; k++) alphaBits |= (ulong)file[offset + 2 + k] << (8 * k);

                // Colour: two RGB565 endpoints, 2-bit indices, always the 4-colour mode in DXT5.
                ushort c0 = BitConverter.ToUInt16(file, offset + 8), c1 = BitConverter.ToUInt16(file, offset + 10);
                Unpack565(c0, colors[..4]);
                Unpack565(c1, colors[4..8]);
                for (int ch = 0; ch < 3; ch++)
                {
                    colors[8 + ch] = (byte)((2 * colors[ch] + colors[4 + ch]) / 3);
                    colors[12 + ch] = (byte)((colors[ch] + 2 * colors[4 + ch]) / 3);
                }
                uint colorBits = BitConverter.ToUInt32(file, offset + 12);

                for (int py = 0; py < 4; py++)
                {
                    int y = by * 4 + py;
                    if (y >= h) break;
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px;
                        if (x >= w) continue;
                        int idx = py * 4 + px;
                        int ci = (int)(colorBits >> (2 * idx)) & 3;
                        int ai = (int)(alphaBits >> (3 * idx)) & 7;
                        int o = (y * w + x) * 4;
                        bgra[o] = colors[ci * 4];
                        bgra[o + 1] = colors[ci * 4 + 1];
                        bgra[o + 2] = colors[ci * 4 + 2];
                        bgra[o + 3] = alpha[ai];
                    }
                }
            }
        }
        return bgra;
    }

    private static int BlockBytes(int w, int h) => Math.Max(1, (Math.Max(1, w) + 3) / 4) * Math.Max(1, (Math.Max(1, h) + 3) / 4) * 16;

    private static void Unpack565(ushort c, Span<byte> bgr)
    {
        int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
        bgr[0] = (byte)((b << 3) | (b >> 2));
        bgr[1] = (byte)((g << 2) | (g >> 4));
        bgr[2] = (byte)((r << 3) | (r >> 2));
        bgr[3] = 255;
    }
}
