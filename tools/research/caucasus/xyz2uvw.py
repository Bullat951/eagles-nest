# Dumps xyz2uvw attributes of a surface5 with the record (shader/class) and the pool translation
# nearest before it.
import re, struct, sys
from collections import Counter

path, show = sys.argv[1], int(sys.argv[2])
f = open(path, "rb")
base = struct.unpack_from("<Q", f.read(16), 8)[0]
f.seek(0)
d = f.read(base)

rec = re.compile(rb"\x02\x00\x00\x00([\x05-\x20])\x00\x00\x00([A-Za-z]+[0-9.]*[0-9])")
starts = [(m.start(), m.group(2).decode()) for m in rec.finditer(d) if len(m.group(2)) == m.group(1)[0]]
pos = [s[0] for s in starts]
import bisect
pat = struct.pack("<II", 1, 7) + b"xyz2uvw"
seen = Counter()
n = 0
for m in re.finditer(re.escape(pat), d):
    o = m.end()
    typ, size, flag = struct.unpack_from("<HHI", d, o)
    nb = struct.unpack_from("<Q", d, o + 8)[0]
    off = int.from_bytes(d[o + 16:o + 22], "little")
    k = bisect.bisect_right(pos, m.start()) - 1
    sh = starts[k][1] if k >= 0 else "?"
    cls_off = starts[k][0] + 8 + len(sh)
    cn = struct.unpack_from("<I", d, cls_off)[0]
    cls = d[cls_off + 4:cls_off + 4 + cn].decode(errors="replace")
    seen[(sh, cls, typ, size, nb)] += 1
    if n < show and (sys.argv[3] if len(sys.argv) > 3 else "") in f"{sh}/{cls}":
        f.seek(base + off)
        raw = f.read(min(nb, 384))
        fl = struct.unpack(f"<{len(raw) // 4}f", raw[:len(raw) // 4 * 4])
        print(f"{sh}/{cls} bytes {nb}:"); [print("    ", [round(v, 5) for v in fl[q:q + 16]]) for q in range(0, len(fl), 16)]
        n += 1
print("kinds:", seen.most_common(8))

