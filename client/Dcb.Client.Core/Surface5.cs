using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Dcb.Client.Core;

/// <summary>Reading a terrain mesh <c>surface/&lt;id&gt;.surface5</c> (docs/DCS_MAP_FORMAT.md,
/// "Terrain surface"): an entry stream of records (`u32 2`, shader, class, …, attributes) and a
/// data block. The arrays found after record k's header belong to record k+1's class; class
/// records hold `baseIndex` and `TRI` into the vertex pool `Pbase` of the next span that has
/// one; a pool's node translation is the identity matrix in the PREVIOUS pool span.</summary>
internal static class Surface5
{
    public readonly record struct Attr(long Bytes, long Offset);

    /// <summary>One record span of the entry stream: its shader, class and the attributes found in it.</summary>
    public sealed class Span
    {
        public int Start;
        public string Shader = "";
        public string Class = "";
        public Attr? BaseIndex, Tri, Pbase;
        public (double X, double Z)? Translation;
    }

    /// <summary>Opens the file and reads its entry stream; returns the data block's offset.</summary>
    public static (SafeFileHandle Handle, long DataBase, byte[] Stream) Open(string path)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try
        {
            Span<byte> head = stackalloc byte[16];
            RandomAccess.Read(handle, head, 0);
            var dataBase = (long)BitConverter.ToUInt64(head[8..]);
            if (dataBase <= 0 || dataBase > int.MaxValue) throw new InvalidDataException("unexpected surface5 header");
            var stream = new byte[dataBase];
            ReadFully(handle, stream, 0);
            return (handle, dataBase, stream);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>The record spans in file order. With <paramref name="shaders"/> only records of
    /// those shaders start a span; null takes every shader-like name (letters, then a version).</summary>
    public static List<Span> Spans(byte[] d, IReadOnlyList<string>? shaders)
    {
        var starts = new List<(int Pos, string Shader, string Class)>();
        if (shaders is not null)
        {
            foreach (var sh in shaders)
            {
                var pat = new byte[8 + sh.Length];
                BitConverter.TryWriteBytes(pat.AsSpan(0), 2);
                BitConverter.TryWriteBytes(pat.AsSpan(4), sh.Length);
                Encoding.ASCII.GetBytes(sh, pat.AsSpan(8));
                for (int at = 0; ;)
                {
                    var i = d.AsSpan(at).IndexOf(pat);
                    if (i < 0) break;
                    var p = at + i;
                    if (ClassAt(d, p + pat.Length) is { } cls) starts.Add((p, sh, cls));
                    at = p + 1;
                }
            }
            starts.Sort((a, b) => a.Pos.CompareTo(b.Pos));
        }
        else
        {
            ReadOnlySpan<byte> two = [2, 0, 0, 0];
            for (int at = 0; ;)
            {
                var i = d.AsSpan(at).IndexOf(two);
                if (i < 0) break;
                var p = at + i;
                at = p + 1;
                if (p + 8 > d.Length) break;
                var n = BitConverter.ToInt32(d, p + 4);
                if (n is < 5 or > 32 || p + 8 + n + 4 > d.Length || !IsShaderName(d.AsSpan(p + 8, n))) continue;
                if (ClassAt(d, p + 8 + n) is { } cls) starts.Add((p, Encoding.ASCII.GetString(d, p + 8, n), cls));
            }
        }

        var spans = new List<Span>(starts.Count);
        for (int k = 0; k < starts.Count; k++)
        {
            int a = starts[k].Pos, b = k + 1 < starts.Count ? starts[k + 1].Pos : d.Length;
            var s = new Span { Start = a, Shader = starts[k].Shader, Class = starts[k].Class };
            var region = d.AsSpan(a, b - a);
            s.BaseIndex = FindAttr(region, "baseIndex");
            s.Tri = FindAttr(region, "TRI");
            s.Pbase = FindAttr(region, "Pbase");
            var m = region.IndexOf(IdentityRotation);
            if (m >= 0 && m + 64 <= region.Length)
                s.Translation = (BitConverter.ToSingle(region[(m + 48)..]), BitConverter.ToSingle(region[(m + 56)..]));
            spans.Add(s);
        }
        return spans;
    }

    /// <summary>The length-prefixed class name at <paramref name="o"/>, or null.</summary>
    private static string? ClassAt(byte[] d, int o)
    {
        if (o + 4 > d.Length) return null;
        var n = BitConverter.ToInt32(d, o);
        if (n is <= 0 or >= 40 || o + 4 + n > d.Length) return null;
        foreach (var c in d.AsSpan(o + 4, n))
            if (!(c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or (byte)'_')) return null;
        return Encoding.ASCII.GetString(d, o + 4, n);
    }

    /// <summary>"Runway5.1", "MeshAssetManifold5": letters, then digits with at most one dot.</summary>
    private static bool IsShaderName(ReadOnlySpan<byte> s)
    {
        int i = 0;
        while (i < s.Length && (s[i] is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z')) i++;
        if (i == 0 || i == s.Length) return false;
        var dots = 0;
        for (; i < s.Length; i++)
        {
            if (s[i] == '.') { if (++dots > 1) return false; }
            else if (s[i] is < (byte)'0' or > (byte)'9') return false;
        }
        return s[^1] != '.';
    }

    // 4×4 float matrix, identity rotation; the translation follows at elements 12..14.
    private static readonly byte[] IdentityRotation = BuildIdentity();

    private static byte[] BuildIdentity()
    {
        byte[] one = [0, 0, 0x80, 0x3f];
        return [.. one, .. new byte[16], .. one, .. new byte[16], .. one];
    }

    /// <summary>`u32 1, u32 len, name, u16 type, u16 size, u32 flag, u64 bytes, u48 offset`.</summary>
    private static Attr? FindAttr(ReadOnlySpan<byte> region, string name)
    {
        Span<byte> pat = stackalloc byte[8 + name.Length];
        BitConverter.TryWriteBytes(pat, 1);
        BitConverter.TryWriteBytes(pat[4..], name.Length);
        Encoding.ASCII.GetBytes(name, pat[8..]);
        var i = region.IndexOf(pat);
        if (i < 0) return null;
        var o = i + pat.Length;
        if (o + 22 > region.Length) return null;
        var bytes = (long)BitConverter.ToUInt64(region[(o + 8)..]);
        long offset = 0;
        for (int b = 5; b >= 0; b--) offset = (offset << 8) | region[o + 16 + b];
        return new Attr(bytes, offset);
    }

    public static void ReadFully(SafeFileHandle h, byte[] buffer, long position)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            var n = RandomAccess.Read(h, buffer.AsSpan(done), position + done);
            if (n <= 0) throw new EndOfStreamException();
            done += n;
        }
    }

    public static float[] ReadFloats(SafeFileHandle h, long dataBase, Attr a)
    {
        var raw = new byte[a.Bytes];
        ReadFully(h, raw, dataBase + a.Offset);
        var f = new float[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, f, 0, f.Length * 4);
        return f;
    }

    public static uint[] ReadUInts(SafeFileHandle h, long dataBase, Attr a)
    {
        var raw = new byte[a.Bytes];
        ReadFully(h, raw, dataBase + a.Offset);
        var u = new uint[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, u, 0, u.Length * 4);
        return u;
    }

    /// <summary>The widest node of the finest level of detail, from the node bboxes after the
    /// matrices of the entry stream. Levels double in size and the finest differs per terrain
    /// (Syria 8–14 km wide with skirts, then 17 km and up; Caucasus 17–26 km, then 29 km and
    /// up): from the narrowest 1 % up to the first gap of over 1 km between node widths.</summary>
    public static double FinestWidth(byte[] stream)
    {
        var widths = new List<float>();
        for (int at = 0; ;)
        {
            var i = stream.AsSpan(at).IndexOf(IdentityRotation);
            if (i < 0) break;
            var p = at + i;
            at = p + IdentityRotation.Length;
            if (p + 64 + 24 > stream.Length) break;
            float x0 = BitConverter.ToSingle(stream, p + 64), z0 = BitConverter.ToSingle(stream, p + 72);
            float x1 = BitConverter.ToSingle(stream, p + 76), z1 = BitConverter.ToSingle(stream, p + 84);
            var w = Math.Max(x1 - x0, z1 - z0);
            if (w is > 1000 and < 2_000_000) widths.Add(w);
        }
        if (widths.Count == 0) return 16000;  // the old fixed rule: vertices within 8 km
        widths.Sort();
        var narrow = widths[widths.Count / 100];
        for (int i = widths.Count / 100; i + 1 < widths.Count && widths[i] < narrow * 2.2; i++)
            if (widths[i + 1] - widths[i] > 1000) return widths[i] + 1;
        return narrow * 1.6;
    }

    /// <summary>The file also holds coarser levels of detail that repeat the fine areas with
    /// simplified outlines: only pools of the finest level (<see cref="FinestWidth"/>) are used.</summary>
    public static bool IsFinest(float[] pool, double finestWidth)
    {
        float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
        for (int i = 0; i + 2 < pool.Length; i += 3)
        {
            x0 = Math.Min(x0, pool[i]); x1 = Math.Max(x1, pool[i]);
            z0 = Math.Min(z0, pool[i + 2]); z1 = Math.Max(z1, pool[i + 2]);
        }
        return Math.Max(x1 - x0, z1 - z0) <= finestWidth;
    }
}
