# For records of chosen shader/class near a point: attribute list, uvw ranges vs world size,
# materialParamsIndex values. Record arrays belong to the NEXT record's class (Surface5 rule).
import re, struct, sys
from collections import Counter

path, X, Z, R = sys.argv[1], float(sys.argv[2]), float(sys.argv[3]), float(sys.argv[4])
want = set(sys.argv[5].split(","))
f = open(path, "rb")
base = struct.unpack_from("<Q", f.read(16), 8)[0]
f.seek(0)
d = f.read(base)
rec = re.compile(rb"\x02\x00\x00\x00([\x05-\x20])\x00\x00\x00([A-Za-z]+[0-9.]*[0-9])")
starts = []
for m in rec.finditer(d):
    if len(m.group(2)) != m.group(1)[0]:
        continue
    q = m.end(); cn = struct.unpack_from("<I", d, q)[0]
    if not 0 < cn < 40:
        continue
    cls = d[q + 4:q + 4 + cn]
    if re.fullmatch(rb"[A-Za-z_]+", cls):
        starts.append((m.start(), m.group(2).decode(), cls.decode()))
ident = b"\x00\x00\x80\x3f" + b"\x00" * 16 + b"\x00\x00\x80\x3f" + b"\x00" * 16 + b"\x00\x00\x80\x3f"
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
        off = int.from_bytes(span[o + 16:o + 22], "little")
        out[m.group(2).decode()] = (typ, size, nb, off)
    return out


def read(a, fmt):
    f.seek(base + a[3]); raw = f.read(a[2]); sz = struct.calcsize("<" + fmt)
    return struct.unpack("<" + fmt * (len(raw) // sz), raw[:len(raw) // sz * sz])


spans = []
for k, (p, sh, cl) in enumerate(starts):
    e = starts[k + 1][0] if k + 1 < len(starts) else len(d)
    s = d[p:e]; m = s.find(ident)
    t = struct.unpack_from("<3f", s, m + 48) if m >= 0 and m + 64 <= len(s) else None
    spans.append((sh, cl, attrs(s), t))

pending, translation, shown = [], None, Counter()
for k, (sh, cl, at, t) in enumerate(spans):
    nxt = spans[k + 1] if k + 1 < len(spans) else None
    if "baseIndex" in at and "TRI" in at and nxt:
        pending.append((nxt[0], nxt[1], at))
    if "Pbase" not in at:
        continue
    if pending and translation and abs(translation[0] - X) < R and abs(translation[2] - Z) < R:
        pool = read(at["Pbase"], "fff")
        if max(abs(v) for v in pool[0::3]) < 13000:
            for psh, pcl, a in pending:
                key = f"{psh}/{pcl}"
                if key not in want or shown[key] >= 2:
                    continue
                shown[key] += 1
                idx = read(a["baseIndex"], "I")
                print(f"== {key}: {len(idx)} verts; attrs " + ", ".join(f"{n}(t{v[0]} s{v[1]} {v[2]}B)" for n, v in a.items() if n not in ('shaderDefine', 'NODEFINITIONS')))
                xs = [pool[3 * i] for i in idx]; zs = [pool[3 * i + 2] for i in idx]
                print(f"   world extent {max(xs) - min(xs):.0f} x {max(zs) - min(zs):.0f} m")
                if "parametricUVW" in a and a["parametricUVW"][1] == 12:
                    uv = read(a["parametricUVW"], "fff")
                    us, vs, ws = uv[0::3], uv[1::3], uv[2::3]
                    print(f"   uvw u {min(us):.3f}..{max(us):.3f} v {min(vs):.3f}..{max(vs):.3f} w {min(ws):.3f}..{max(ws):.3f}")
                    for q in range(0, len(idx), max(1, len(idx) // 4)):
                        print(f"     world ({xs[q]:.0f},{zs[q]:.0f}) uvw ({us[q]:.4f},{vs[q]:.4f},{ws[q]:.4f})")
                if "parametricUVW" in a and a["parametricUVW"][1] == 4:
                    raw4 = read(a["parametricUVW"], "I")
                    hh = read(a["parametricUVW"], "e")
                    us, vs = hh[0::2], hh[1::2]
                    print(f"   uvw as u32 sample {raw4[:6]}; as 2xhalf u {min(us):.3f}..{max(us):.3f} v {min(vs):.3f}..{max(vs):.3f}")
                    for q in range(0, len(idx), max(1, len(idx) // 5)):
                        print(f"     world ({xs[q]:.0f},{zs[q]:.0f}) half-uv ({us[q]:.4f},{vs[q]:.4f}) u32 {raw4[q]}")
                if "assetIndex" in a:
                    ai = read(a["assetIndex"], "I")
                    print(f"   assetIndex: {len(ai)} values, {Counter(ai).most_common(6)}")
                if "materialParamsIndex" in a:
                    mp = read(a["materialParamsIndex"], "I")
                    print(f"   materialParamsIndex values: {Counter(mp).most_common(6)}")
                if "offset" in a:
                    print(f"   offset: {Counter(read(a['offset'], 'f')).most_common(4)}")
    pending = []
    translation = t
    if len(shown) >= len(want) and all(v >= 2 for v in shown.values()):
        break



