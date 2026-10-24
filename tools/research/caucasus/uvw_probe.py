# surface5: for land records near a point, compare vertex world position (Pbase + pool translation)
# with the record's parametricUVW. Same record/pool rules as Core/Surface5.cs and Pavement.cs:
# arrays after record k belong to record k+1; a pool's translation is the matrix in the previous
# pool span.
import re, struct, sys

path, X, Z, R = sys.argv[1], float(sys.argv[2]), float(sys.argv[3]), float(sys.argv[4])
f = open(path, "rb")
base = struct.unpack_from("<Q", f.read(16), 8)[0]
f.seek(0)
d = f.read(base)

rec = re.compile(rb"\x02\x00\x00\x00([\x05-\x20])\x00\x00\x00([A-Za-z]+[0-9.]*[0-9])")
starts = []
for m in rec.finditer(d):
    n = m.group(1)[0]
    if len(m.group(2)) != n:
        continue
    q = m.end()
    cn = struct.unpack_from("<I", d, q)[0]
    if not 0 < cn < 40:
        continue
    cls = d[q + 4:q + 4 + cn]
    if not re.fullmatch(rb"[A-Za-z_]+", cls):
        continue
    starts.append((m.start(), m.group(2).decode(), cls.decode()))

ident = b"\x00\x00\x80\x3f" + b"\x00" * 16 + b"\x00\x00\x80\x3f" + b"\x00" * 16 + b"\x00\x00\x80\x3f"


def attr(span, name):
    pat = struct.pack("<II", 1, len(name)) + name
    i = span.find(pat)
    if i < 0:
        return None
    o = i + len(pat)
    typ, size, flag = struct.unpack_from("<HHI", span, o)
    nb = struct.unpack_from("<Q", span, o + 8)[0]
    off = int.from_bytes(span[o + 16:o + 22], "little")
    return typ, size, nb, off


def read(a, fmt):
    f.seek(base + a[3])
    raw = f.read(a[2])
    n = len(raw) // struct.calcsize("<" + fmt)
    return struct.unpack("<" + fmt * n, raw[:n * struct.calcsize("<" + fmt)])


spans = []
for k, (p, sh, cl) in enumerate(starts):
    e = starts[k + 1][0] if k + 1 < len(starts) else len(d)
    s = d[p:e]
    m = s.find(ident)
    t = struct.unpack_from("<3f", s, m + 48) if m >= 0 and m + 64 <= len(s) else None
    spans.append(dict(sh=sh, cl=cl, bi=attr(s, b"baseIndex"), tri=attr(s, b"TRI"), uvw=attr(s, b"parametricUVW"), pb=attr(s, b"Pbase"), t=t))

pending, translation, shown = [], None, 0
for k, s in enumerate(spans):
    nxt = spans[k + 1] if k + 1 < len(spans) else None
    if s["bi"] and s["uvw"] and nxt:
        pending.append((nxt["sh"], nxt["cl"], s["bi"], s["uvw"]))
    if not s["pb"]:
        continue
    if pending and translation and abs(translation[0] - X) < R and abs(translation[2] - Z) < R:
        pool = read(s["pb"], "fff")
        xs = pool[0::3]
        if max(abs(v) for v in xs) < 13000:   # finest Caucasus level (17-26 km wide)
            for sh, cl, bi, uvw in pending:
                if cl not in ("Land", "Field", "Forest") or shown >= 6:
                    continue
                idx = read(bi, "I")
                uv = read(uvw, "fff")
                print(f"pool t=({translation[0]:.0f},{translation[2]:.0f}) record {sh}/{cl}: {len(idx)} indices, uvw {uvw[2]} B")
                for q in range(0, min(len(idx), len(uv) // 3), max(1, len(idx) // 5)):
                    v = idx[q]
                    wx, wz = pool[3 * v] + translation[0], pool[3 * v + 2] + translation[2]
                    print(f"    world ({wx:10.1f}, {wz:10.1f})  uvw ({uv[3 * q]:.6f}, {uv[3 * q + 1]:.6f}, {uv[3 * q + 2]:.6f})")
                shown += 1
    pending = []
    translation = s["t"]
    if shown >= 6:
        break
