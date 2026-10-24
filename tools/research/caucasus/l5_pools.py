# Pbase pools of a surface5: extent of the local vertices, matrix in the span, LOD by |x| max.
import re, struct, sys
from collections import Counter

path, show = sys.argv[1], int(sys.argv[2])
f = open(path, "rb")
base = struct.unpack_from("<Q", f.read(16), 8)[0]
f.seek(0)
d = f.read(base)

pat = b"\x01\x00\x00\x00\x05\x00\x00\x00Pbase"
ident_f = b"\x00\x00\x80\x3f" + b"\x00" * 16 + b"\x00\x00\x80\x3f" + b"\x00" * 16 + b"\x00\x00\x80\x3f"
ident_d = b"\x00" * 6 + b"\xf0\x3f" + b"\x00" * 32 + b"\x00" * 6 + b"\xf0\x3f" + b"\x00" * 32 + b"\x00" * 6 + b"\xf0\x3f"
pools, prev = [], 0
for m in re.finditer(re.escape(pat), d):
    o = m.end()
    nbytes = struct.unpack_from("<Q", d, o + 8)[0]
    off = int.from_bytes(d[o + 16:o + 22], "little")
    span = d[prev:m.start()]
    fi, di = span.rfind(ident_f), span.rfind(ident_d)
    t = None
    if fi >= 0:
        t = struct.unpack_from("<3f", span, fi + 48)
    pools.append((m.start(), nbytes, off, t, di >= 0))
    prev = m.start()

ext = Counter()
for k, (p, nb, off, t, dbl) in enumerate(pools):
    if k < show or k % 500 == 0:
        f.seek(base + off)
        v = struct.unpack(f"<{nb // 4}f", f.read(nb - nb % 4))
        xs, zs = v[0::3], v[2::3]
        mx = max(max(abs(a) for a in xs), max(abs(a) for a in zs))
        print(f"pool {k} @{p}: {nb // 12} verts, x {min(xs):.0f}..{max(xs):.0f} z {min(zs):.0f}..{max(zs):.0f} (|max| {mx:.0f}); "
              f"float matrix before: {t is not None}{' t=(%.0f, %.0f, %.0f)' % t if t else ''}; double matrix: {dbl}")
print(f"{len(pools)} pools")
