# Hypothesis test: a Town vertex's parametricUVW (u32) indexes the node's manifold arrays
# (P / N / tangent / binormal / xyz2uvw found in the node's spans). For each Town record, list the
# node's arrays and their lengths, then fit world (x, z) -> P[index] per manifold index and report
# scale/rotation/residual. Record/pool rules as Core/Surface5.cs.
import re, struct, sys, math
from collections import defaultdict

path, show = sys.argv[1], int(sys.argv[2])
f = open(path, "rb")
base = struct.unpack_from("<Q", f.read(16), 8)[0]
f.seek(0)
d = f.read(base)

rec = re.compile(rb"\x02\x00\x00\x00([\x05-\x20])\x00\x00\x00([A-Za-z]+[0-9.]*[0-9])")
starts = []
for m in rec.finditer(d):
    if len(m.group(2)) != m.group(1)[0]:
        continue
    q = m.end()
    cn = struct.unpack_from("<I", d, q)[0]
    if 0 < cn < 40 and re.fullmatch(rb"[A-Za-z_]+", d[q + 4:q + 4 + cn]):
        starts.append((m.start(), m.group(2).decode(), d[q + 4:q + 4 + cn].decode()))
attr_re = re.compile(rb"\x01\x00\x00\x00([\x01-\x20])\x00\x00\x00([A-Za-z_][A-Za-z0-9_]*)")
ident = b"\x00\x00\x80\x3f" + b"\x00" * 16 + b"\x00\x00\x80\x3f" + b"\x00" * 16 + b"\x00\x00\x80\x3f"


def attrs(span):
    out = {}
    for m in attr_re.finditer(span):
        if len(m.group(2)) != m.group(1)[0]:
            continue
        o = m.end()
        if o + 22 > len(span):
            continue
        typ, size, flag = struct.unpack_from("<HHI", span, o)
        nb = struct.unpack_from("<Q", span, o + 8)[0]
        off = int.from_bytes(span[o + 16:o + 22], "little")
        out.setdefault(m.group(2).decode(), (typ, size, nb, off))
    return out


def read(a, fmt):
    f.seek(base + a[3])
    raw = f.read(a[2])
    s = struct.calcsize("<" + fmt)
    return struct.unpack("<" + fmt * (len(raw) // s), raw[:len(raw) // s * s])


spans = []
for k, (p, sh, cl) in enumerate(starts):
    e = starts[k + 1][0] if k + 1 < len(starts) else len(d)
    s = d[p:e]
    m = s.find(ident)
    t = struct.unpack_from("<3f", s, m + 48) if m >= 0 and m + 64 <= len(s) else None
    spans.append(dict(sh=sh, cl=cl, A=attrs(s), t=t))

shown = 0
translation = None
node_spans = []
for k, s in enumerate(spans):
    node_spans.append(k)
    if "Pbase" not in s["A"]:
        continue
    # node complete: spans node_spans (records + this pool span)
    towns = [j for j in node_spans if j + 1 < len(spans) and spans[j + 1]["cl"] == "Town" and "TRI" in spans[j]["A"] and "parametricUVW" in spans[j]["A"]]
    if towns and translation:
        pool = read(s["A"]["Pbase"], "fff")
        if max(abs(v) for v in pool[0::3]) < 13000:
            arrays = defaultdict(list)
            for j in node_spans:
                for n, a in spans[j]["A"].items():
                    if n in ("P", "N", "tangent", "binormal", "xyz2uvw", "parametricTangent", "parametricBinormal"):
                        arrays[n].append((j, a))
            print(f"== node t=({translation[0]:.0f},{translation[2]:.0f}) spans {node_spans[0]}..{node_spans[-1]}, pool {len(pool)//3} verts")
            for n, lst in arrays.items():
                print(f"   {n}: " + ", ".join(f"span{j}:t{a[0]}s{a[1]} {a[2]}B" for j, a in lst))
            for j in towns[:2]:
                A = spans[j]["A"]
                idx, uvw, tri = read(A["baseIndex"], "I"), read(A["parametricUVW"], "I"), read(A["TRI"], "I")
                mx = max(uvw)
                print(f"   town record {j + 1}: {len(idx)} vertices, manifold index {min(uvw)}..{mx}")
                # Try each P-like array of the node as a per-index uvw table.
                for n, lst in arrays.items():
                    for sj, a in lst:
                        comps = a[1] // 4 if a[1] in (8, 12, 16) else None
                        if comps is None:
                            continue
                        vals = read(a, "f" * comps)
                        count = len(vals) // comps
                        if count <= mx:
                            continue
                        # per manifold index: least squares u = a*x + b*z + c over its vertices
                        groups = defaultdict(list)
                        for v, m_ in zip(idx, uvw):
                            if v * 3 + 2 < len(pool):
                                groups[m_].append((pool[3 * v] + translation[0], pool[3 * v + 2] + translation[2]))
                        print(f"     {n} span{sj}: {count} entries x{comps}; sample P[{min(uvw)}..]: {[round(x, 3) for x in vals[min(uvw) * comps:min(uvw) * comps + 2 * comps]]}")
            shown += 1
    node_spans = []
    translation = s["t"]
    if shown >= show:
        break
