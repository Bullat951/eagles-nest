# Node bboxes after the identity float matrices of a surface5 entry stream: width histogram.
import re, struct, sys
from collections import Counter

path = sys.argv[1]
f = open(path, "rb")
base = struct.unpack_from("<Q", f.read(16), 8)[0]
f.seek(0)
d = f.read(base)
ident = b"\x00\x00\x80\x3f" + b"\x00" * 16 + b"\x00\x00\x80\x3f" + b"\x00" * 16 + b"\x00\x00\x80\x3f"
h = Counter()
n = 0
for m in re.finditer(re.escape(ident), d):
    p = m.start()
    if p + 64 + 24 > len(d):
        continue
    tx, ty, tz = struct.unpack_from("<3f", d, p + 48)
    x0, y0, z0, x1, y1, z1 = struct.unpack_from("<6f", d, p + 64)
    w = max(x1 - x0, z1 - z0)
    n += 1
    h[round(w / 1000)] += 1
    if n <= 4:
        print(f"t=({tx:.0f},{tz:.0f}) bbox x {x0:.0f}..{x1:.0f} z {z0:.0f}..{z1:.0f}")
print(f"{n} matrices; width (km) histogram:")
for k in sorted(h):
    if h[k] >= 3:
        print(f"  {k:5} km: {h[k]}")
