# Every instanced object within R m of a point in a scn5: model name counts (all blocks, not only signs).
import re, struct, sys
from collections import Counter

path, X, Z, R = sys.argv[1], float(sys.argv[2]), float(sys.argv[3]), float(sys.argv[4])
f = open(path, "rb")
base = struct.unpack_from("<Q", f.read(16), 8)[0]
f.seek(0)
d = f.read(base)

names, o = [], 66
while o + 4 <= len(d):
    n = struct.unpack_from("<I", d, o)[0]
    if not 1 <= n <= 80:
        break
    s = d[o + 4:o + 4 + n]
    if any(c < 0x20 or c > 0x7E for c in s):
        break
    names.append(s.decode())
    o += 4 + n
print(f"model table: {len(names)} names, ends at {o}")

one = b"\x00" * 6 + b"\xf0\x3f"
mat = one + b"\x00" * 32 + one + b"\x00" * 32 + one
nodes = [(m.start(),) + struct.unpack_from("<3d", d, m.start() + len(mat) + 8) for m in re.finditer(re.escape(mat), d)]
tail = bytes([4, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0x50, 2, 0, 0x0C])
near, blocks_seen, blocks_other = Counter(), 0, 0
for k, (start, tx, ty, tz) in enumerate(nodes):
    end = nodes[k + 1][0] if k + 1 < len(nodes) else len(d)
    node = d[start:end]
    for m in re.finditer(re.escape(tail), node):
        t = m.start()
        if t < 8 or struct.unpack_from("<i", node, t - 8)[0] != 2:
            continue
        count = struct.unpack_from("<i", node, t - 4)[0]
        p = t + 12
        pos_bytes = struct.unpack_from("<Q", node, p + 9)[0]
        pos_off = int.from_bytes(node[p + 17:p + 23], "little")
        e = node.find(b"TYPE_OPTIONS", p)
        if e < 0 or pos_bytes != count * 12:
            blocks_other += 1
            continue
        e += 12
        typ_off = int.from_bytes(node[e + 16:e + 22], "little")
        f.seek(base + pos_off)
        pos = struct.unpack(f"<{count * 3}f", f.read(count * 12))
        hit = [i for i in range(count) if abs(tx + pos[3 * i] - X) <= R and abs(tz + pos[3 * i + 2] - Z) <= R]
        if not hit:
            continue
        blocks_seen += 1
        f.seek(base + typ_off)
        types = struct.unpack(f"<{count}I", f.read(count * 4))
        for i in hit:
            idx = types[i] & 0xFFFF
            near[names[idx] if idx < len(names) else f"#{idx}"] += 1
print(f"blocks with objects in range: {blocks_seen}; blocks not decoded: {blocks_other}")
for name, c in near.most_common():
    print(f"  {c:5}  {name}")
