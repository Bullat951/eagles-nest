# Dumps Town (HardSplatArray5.1) records of Caucasus.surface5: every attribute of the span with
# its arrays (offset, parametricUVW as u32, assetIndex, xyz2uvw, materialParamsIndex), to find
# how a town triangle's manifold index places the village photo.
# Record rule (Core/Surface5.cs): the arrays found after record k's header belong to record k+1.
import re, struct, sys
from collections import Counter

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


def attrs(span):
    out = {}
    for m in attr_re.finditer(span):
        if len(m.group(2)) != m.group(1)[0]:
            continue
        o = m.end()
        typ, size, flag = struct.unpack_from("<HHI", span, o)
        nb = struct.unpack_from("<Q", span, o + 8)[0]
        off = int.from_bytes(span[o + 16:o + 22], "little")
        out.setdefault(m.group(2).decode(), (typ, size, nb, off))
    return out


def read(a, fmt, limit=None):
    f.seek(base + a[3])
    nb = a[2] if limit is None else min(a[2], limit)
    raw = f.read(nb)
    s = struct.calcsize("<" + fmt)
    return struct.unpack("<" + fmt * (len(raw) // s), raw[:len(raw) // s * s])


shown = 0
for k in range(len(starts) - 1):
    nxt = starts[k + 1]
    if nxt[2] != "Town":
        continue
    span = d[starts[k][0]:nxt[0]]
    A = attrs(span)
    if "TRI" not in A:
        continue
    own = attrs(d[nxt[0]:starts[k + 2][0] if k + 2 < len(starts) else len(d)])
    print(f"== record {k + 1} {nxt[1]}/{nxt[2]}  arrays: " + ", ".join(f"{n}(t{a[0]} s{a[1]} {a[2]}B)" for n, a in A.items()))
    print("   own span attrs: " + ", ".join(f"{n}(t{a[0]} s{a[1]} {a[2]}B)" for n, a in own.items()))
    for name, src in (("offset", A), ("offset", own)):
        if name in src:
            print(f"   {name}: {read(src[name], 'I', 64)}  as f: {[round(v, 4) for v in read(src[name], 'f', 64)]}")
    if "parametricUVW" in A:
        u = read(A["parametricUVW"], "I")
        print(f"   parametricUVW (u32): {len(u)} values, distinct {sorted(Counter(u))[:20]}")
    if "assetIndex" in A:
        a = read(A["assetIndex"], "I")
        print(f"   assetIndex: {len(a)} values, distinct {sorted(Counter(a))[:12]}")
    if "materialParamsIndex" in A:
        mp = read(A["materialParamsIndex"], "I")
        print(f"   materialParamsIndex distinct {sorted(Counter(mp))[:12]}")
    for src, label in ((A, "arrays"), (own, "own")):
        if "xyz2uvw" in src:
            fl = read(src["xyz2uvw"], "f")
            for q in range(0, len(fl), 16):
                print(f"   xyz2uvw[{label}][{q // 16}]: {[round(v, 6) for v in fl[q:q + 16]]}")
    shown += 1
    if shown >= show:
        break
