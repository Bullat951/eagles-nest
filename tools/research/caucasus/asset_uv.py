# Per town asset (village photo): the range of its triangles' (u, w) = node xyz2uvw[manifold] applied
# (row-major) to the vertex's local position. Tells where the photo's origin sits (centre vs corner)
# and, with the photo's pixel size, its metres per pixel.
import re, struct, sys
from collections import defaultdict

path = sys.argv[1]
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


# asset names (u32 count + length-prefixed, anchored on "town_1")
anchor = d.find(b"\x06\x00\x00\x00town_1")
names = []
for back in range(4, 40000):
    p = anchor - back
    cnt = struct.unpack_from("<I", d, p - 4)[0]
    if not 100 <= cnt <= 5000:
        continue
    q, out = p, []
    while len(out) < cnt:
        n = struct.unpack_from("<I", d, q)[0]
        if not 1 <= n <= 80:
            break
        out.append(d[q + 4:q + 4 + n].decode(errors="replace"))
        q += 4 + n
    if len(out) == cnt and "town_1" in out:
        names = out
        break

spans = [attrs(d[p:(starts[k + 1][0] if k + 1 < len(starts) else len(d))]) for k, (p, _, _) in enumerate(starts)]
ranges = defaultdict(lambda: [1e9, -1e9, 1e9, -1e9, 0])
node, nodes = [], 0
for k, A in enumerate(spans):
    node.append(k)
    if "Pbase" not in A:
        continue
    if "xyz2uvw" in A and nodes < 400:
        pool = None
        for j in node:
            if j + 1 >= len(starts) or starts[j + 1][2] != "Town" or "parametricUVW" not in spans[j] or "assetIndex" not in spans[j]:
                continue
            if pool is None:
                pool = read(A["Pbase"], "fff")
                if max(abs(v) for v in pool[0::3]) > 13000:
                    break
                mats = read(A["xyz2uvw"], "f" * 16)
                M = [mats[i * 16:(i + 1) * 16] for i in range(len(mats) // 16)]
                nodes += 1
            S = spans[j]
            idx, man, tri, asset = read(S["baseIndex"], "I"), read(S["parametricUVW"], "I"), read(S["TRI"], "I"), read(S["assetIndex"], "I")
            for t in range(len(tri) // 3):
                a = asset[t] if t < len(asset) else None
                if a is None or a >= len(names):
                    continue
                r = ranges[names[a]]
                for c in range(3):
                    vi = tri[3 * t + c]
                    if vi >= len(idx) or man[vi] >= len(M):
                        continue
                    p = idx[vi]
                    x, y, z = pool[3 * p], pool[3 * p + 1], pool[3 * p + 2]
                    m = M[man[vi]]
                    u = m[0] * x + m[1] * y + m[2] * z + m[3]
                    w = m[8] * x + m[9] * y + m[10] * z + m[11]
                    r[0] = min(r[0], u); r[1] = max(r[1], u); r[2] = min(r[2], w); r[3] = max(r[3], w)
                r[4] += 1
    node = []

print(f"{nodes} nodes with towns scanned")
for name, (u0, u1, w0, w1, n) in sorted(ranges.items(), key=lambda kv: -kv[1][4])[:25]:
    print(f"  {name:16} {n:6} tris  u {u0:8.1f}..{u1:8.1f} ({u1 - u0:6.0f} m)  w {w0:8.1f}..{w1:8.1f} ({w1 - w0:6.0f} m)")
