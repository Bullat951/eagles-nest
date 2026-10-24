# Town vertex -> uvw: parametricUVW (u32) indexes the node's xyz2uvw (4x4 float matrices in the pool
# span, with tangent/binormal). Applies matrix k to the vertex's local (pool) and world position and
# prints the ranges per manifold and the metres-per-uv scale, to decide row/column-major and frame.
import re, struct, sys
from collections import defaultdict

path, node_x, node_z = sys.argv[1], float(sys.argv[2]), float(sys.argv[3])
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
        out.setdefault(m.group(2).decode(), (typ, size, nb, int.from_bytes(span[o + 16:o + 22], "little")))
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
    spans.append(dict(cl=cl, A=attrs(s), t=t))

translation, node = None, []
for k, s in enumerate(spans):
    node.append(k)
    if "Pbase" not in s["A"]:
        continue
    if translation and abs(translation[0] - node_x) < 1 and abs(translation[2] - node_z) < 1:
        pool = read(s["A"]["Pbase"], "fff")
        mats = read(s["A"]["xyz2uvw"], "f" * 16)
        M = [mats[i * 16:(i + 1) * 16] for i in range(len(mats) // 16)]
        print(f"node ({node_x:.0f},{node_z:.0f}): {len(M)} matrices; first three:")
        for i in range(3):
            print("   ", [round(v, 6) for v in M[i]])
        for j in node:
            if j + 1 >= len(spans) or spans[j + 1]["cl"] != "Town" or "parametricUVW" not in spans[j]["A"]:
                continue
            A = spans[j]["A"]
            idx, man = read(A["baseIndex"], "I"), read(A["parametricUVW"], "I")
            groups = defaultdict(list)
            for v, mi in zip(idx, man):
                groups[mi].append((pool[3 * v], pool[3 * v + 1], pool[3 * v + 2]))
            for mi in sorted(groups)[:6]:
                pts = groups[mi]
                m = M[mi]
                for label, frame in (("local", (0, 0)), ("world", (translation[0], translation[2]))):
                    for major in ("row", "col"):
                        us, vs, ws = [], [], []
                        for (x, y, z) in pts:
                            X, Y, Z = x + frame[0], y, z + frame[1]
                            if major == "row":
                                u = m[0] * X + m[1] * Y + m[2] * Z + m[3]; v = m[4] * X + m[5] * Y + m[6] * Z + m[7]; w = m[8] * X + m[9] * Y + m[10] * Z + m[11]
                            else:
                                u = m[0] * X + m[4] * Y + m[8] * Z + m[12]; v = m[1] * X + m[5] * Y + m[9] * Z + m[13]; w = m[2] * X + m[6] * Y + m[10] * Z + m[14]
                            us.append(u); vs.append(v); ws.append(w)
                        xs = [p[0] for p in pts]; zs = [p[2] for p in pts]
                        span_m = max(max(xs) - min(xs), max(zs) - min(zs))
                        print(f"   manifold {mi:3} ({len(pts)} v, {span_m:6.0f} m) {label:5} {major}: u {min(us):9.3f}..{max(us):9.3f}  v {min(vs):9.3f}..{max(vs):9.3f}  w {min(ws):8.3f}..{max(ws):8.3f}")
            break
        break
    node = []
    translation = s["t"]
