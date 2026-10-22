using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Dcb.Client.Core;

/// <summary>Taxiway and runway signs DCS places at airfields, read from the terrain's scene file
/// <c>Scenes/&lt;id&gt;.scn5</c> (docs/DCS_MAP_FORMAT.md, "Scene objects"). The sign models are
/// named after their face: <c>a_black</c> a location sign (on taxiway A), <c>yellow_a</c> a
/// direction sign (to taxiway A), <c>course_3</c> a runway designator sign.</summary>
public sealed class AirfieldSigns : IDisposable
{
    public enum Kind { Location, Direction, Runway, Tower }

    /// <summary>Control tower models of airfields (Syria; city towers left out).</summary>
    private static readonly HashSet<string> TowerModels =
        ["kdp", "kdp_small", "atc_control_tower", "airport_building1_tower", "damascus_terminal_tower", "chukurova_tower", "nevatim_tower", "syria_tower"];

    /// <summary>A placed sign: what it says and where it stands (DCS map metres).</summary>
    public readonly record struct Sign(Kind Kind, string Text, double X, double Z);

    private readonly SafeFileHandle _handle;
    private readonly long _dataBase;
    private readonly byte[] _stream;
    private readonly List<(int Offset, double X, double Z)> _nodes;
    private readonly Dictionary<int, (Kind Kind, string Text)> _signTypes;

    private AirfieldSigns(SafeFileHandle handle, long dataBase, byte[] stream, List<(int, double, double)> nodes, Dictionary<int, (Kind, string)> signTypes)
    {
        (_handle, _dataBase, _stream, _nodes, _signTypes) = (handle, dataBase, stream, nodes, signTypes);
    }

    public int NodeCount => _nodes.Count;

    /// <summary>Reads the scene's entry stream (Syria ~100 MB), its model names and node positions.</summary>
    public static AirfieldSigns Open(string path)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try
        {
            Span<byte> head = stackalloc byte[16];
            RandomAccess.Read(handle, head, 0);
            var dataBase = (long)BitConverter.ToUInt64(head[8..]);
            if (dataBase <= 0 || dataBase > int.MaxValue) throw new InvalidDataException("unexpected scn5 header");
            var stream = new byte[dataBase];
            Surface5.ReadFully(handle, stream, 0);
            return new AirfieldSigns(handle, dataBase, stream, Nodes(stream), SignTypes(ModelNames(stream)));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>The model-name table: length-prefixed names from offset 66; a model's index is
    /// the low 16 bits of an instance's TYPE_OPTIONS.</summary>
    public static List<string> ModelNames(byte[] d)
    {
        var names = new List<string>();
        for (int o = 66; o + 4 <= d.Length;)
        {
            var n = BitConverter.ToInt32(d, o);
            if (n is < 1 or > 80 || o + 4 + n > d.Length) break;
            var s = d.AsSpan(o + 4, n);
            foreach (var c in s) if (c is < 0x20 or > 0x7e) return names;
            names.Add(Encoding.ASCII.GetString(s));
            o += 4 + n;
        }
        return names;
    }

    /// <summary>Sign models by index: their kind and the letter or digit on their face.</summary>
    public static Dictionary<int, (Kind, string)> SignTypes(IReadOnlyList<string> names)
    {
        var map = new Dictionary<int, (Kind, string)>();
        for (int i = 0; i < names.Count; i++)
        {
            var s = names[i].ToLowerInvariant();
            if (s.Length == 7 && s.EndsWith("_black") && char.IsLetterOrDigit(s[0])) map[i] = (Kind.Location, s[..1].ToUpperInvariant());
            else if (s.Length == 8 && s.StartsWith("yellow_") && char.IsLetterOrDigit(s[7])) map[i] = (Kind.Direction, s[7..].ToUpperInvariant());
            else if (s.StartsWith("course_")) map[i] = (Kind.Runway, s[7..] switch { "dash" => "-", var t => t.ToUpperInvariant() });
            else if (TowerModels.Contains(s)) map[i] = (Kind.Tower, s);
        }
        return map;
    }

    // A node: identity 4×4 double matrix (1.0 at elements 0, 5, 10), translation at 12..14.
    private static readonly byte[] NodeMatrix = BuildMatrix();

    private static byte[] BuildMatrix()
    {
        byte[] one = [0, 0, 0, 0, 0, 0, 0xf0, 0x3f];
        return [.. one, .. new byte[32], .. one, .. new byte[32], .. one];
    }

    private static List<(int, double, double)> Nodes(byte[] d)
    {
        var nodes = new List<(int, double, double)>();
        for (int at = 0; ;)
        {
            var i = d.AsSpan(at).IndexOf(NodeMatrix);
            if (i < 0) break;
            var p = at + i;
            var t = p + NodeMatrix.Length + 8;  // past element 11
            if (t + 24 <= d.Length) nodes.Add((p, BitConverter.ToDouble(d, t), BitConverter.ToDouble(d, t + 16)));
            at = p + NodeMatrix.Length;
        }
        return nodes;
    }

    // Instance block: u32 2, u32 count, u32 4 (attributes), u32 1, u32 1, then the position
    // attribute "50 02 00 0c", 5 zero bytes, u64 bytes, u48 offset (float3, node-local).
    private static readonly byte[] BlockTail = [4, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0x50, 0x02, 0x00, 0x0c];
    private static readonly byte[] TypeOptions = "TYPE_OPTIONS"u8.ToArray();

    /// <summary>Signs placed within <paramref name="radius"/> m of a point (by node: nodes are
    /// at most ~1 km across).</summary>
    public IReadOnlyList<Sign> Near(double x, double z, double radius)
    {
        var signs = new List<Sign>();
        for (int k = 0; k < _nodes.Count; k++)
        {
            var (start, tx, tz) = _nodes[k];
            if (Math.Abs(tx - x) > radius + 1000 || Math.Abs(tz - z) > radius + 1000) continue;
            var end = k + 1 < _nodes.Count ? _nodes[k + 1].Offset : _stream.Length;
            var node = _stream.AsSpan(start, end - start);
            for (int at = 0; ;)
            {
                var i = node[at..].IndexOf(BlockTail);
                if (i < 0) break;
                var tail = at + i;
                at = tail + BlockTail.Length;
                if (tail < 8 || BitConverter.ToInt32(node[(tail - 8)..]) != 2) continue;
                var count = BitConverter.ToInt32(node[(tail - 4)..]);
                var p = tail + 12;  // "50 02 00 0c"
                if (p + 23 > node.Length || count <= 0) continue;
                var posBytes = (long)BitConverter.ToUInt64(node[(p + 9)..]);
                var posOffset = U48(node[(p + 17)..]);
                var t = node[p..].IndexOf(TypeOptions);
                if (t < 0) continue;
                var e = p + t + TypeOptions.Length;
                if (e + 22 > node.Length) continue;
                var typeBytes = (long)BitConverter.ToUInt64(node[(e + 8)..]);
                var typeOffset = U48(node[(e + 16)..]);
                if (posBytes != count * 12L || typeBytes != count * 4L) continue;

                var types = Read(typeOffset, typeBytes);
                float[]? pos = null;
                for (int n = 0; n < count; n++)
                {
                    if (!_signTypes.TryGetValue((int)(BitConverter.ToUInt32(types, 4 * n) & 0xffff), out var sign)) continue;
                    pos ??= Floats(Read(posOffset, posBytes));
                    double sx = tx + pos[3 * n], sz = tz + pos[3 * n + 2];
                    if (Math.Abs(sx - x) <= radius && Math.Abs(sz - z) <= radius) signs.Add(new Sign(sign.Kind, sign.Text, sx, sz));
                }
            }
        }
        return signs;
    }

    private static long U48(ReadOnlySpan<byte> b)
    {
        long v = 0;
        for (int i = 5; i >= 0; i--) v = (v << 8) | b[i];
        return v;
    }

    private byte[] Read(long offset, long bytes)
    {
        var raw = new byte[bytes];
        Surface5.ReadFully(_handle, raw, _dataBase + offset);
        return raw;
    }

    private static float[] Floats(byte[] raw)
    {
        var f = new float[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, f, 0, f.Length * 4);
        return f;
    }

    /// <summary>A digit panel this close to a letter panel is part of the same board ("A" + "1"
    /// = taxiway A1); panels stand 0.8 m apart.</summary>
    private const double PanelM = 1.5;

    /// <summary>Location signs as taxiway names: letter and digit panels of one board joined
    /// ("A1"), lone digits dropped, then signs with the same name within <paramref name="mergeM"/>
    /// m (the pair facing both ways) merged into their mean.</summary>
    public static IReadOnlyList<Sign> TaxiwayLetters(IEnumerable<Sign> signs, double mergeM = 80)
    {
        var panels = signs.Where(s => s.Kind == Kind.Location).ToList();
        var letters = panels.Where(s => char.IsLetter(s.Text[0])).ToList();
        var names = new List<Sign>(letters);
        foreach (var d in panels.Where(s => char.IsDigit(s.Text[0])))
        {
            var i = names.FindIndex(l => char.IsLetter(l.Text[^1]) && (l.X - d.X) * (l.X - d.X) + (l.Z - d.Z) * (l.Z - d.Z) < PanelM * PanelM);
            if (i >= 0) names[i] = names[i] with { Text = names[i].Text + d.Text };
        }

        var groups = new List<List<Sign>>();
        foreach (var s in names)
        {
            var g = groups.FirstOrDefault(g => g[0].Text == s.Text && g.Any(o => Math.Abs(o.X - s.X) < mergeM && Math.Abs(o.Z - s.Z) < mergeM
                && (o.X - s.X) * (o.X - s.X) + (o.Z - s.Z) * (o.Z - s.Z) < mergeM * mergeM));
            if (g is null) groups.Add([s]);
            else g.Add(s);
        }
        return groups.Select(g => new Sign(Kind.Location, g[0].Text, g.Average(s => s.X), g.Average(s => s.Z))).ToList();
    }

    public void Dispose() => _handle.Dispose();
}
