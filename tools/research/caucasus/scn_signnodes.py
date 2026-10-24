# Which scene nodes hold the sign models, and how far the signs are from the node translation.
import re, struct, sys

path = sys.argv[1]
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
sign = {i for i, s in enumerate(names) if s.endswith("_block") or s.startswith("course_") or s in ("kdp", "batumi_kdp")}

one = b"\x00" * 6 + b"\xf0\x3f"
mat = one + b"\x00" * 32 + one + b"\x00" * 32 + one
nodes = [(m.start(),) + struct.unpack_from("<3d", d, m.start() + len(mat) + 8) for m in re.finditer(re.escape(mat), d)]
tail = bytes([4, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0x50, 2, 0, 0x0C])


def u48(b, o):
    return int.from_bytes(b[o:o + 6], "little")


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
        pos_off = u48(node, p + 17)
        e = node.find(b"TYPE_OPTIONS", p)
        if e < 0:
            continue
        e += 12
        typ_bytes = struct.unpack_from("<Q", node, e + 8)[0]
        typ_off = u48(node, e + 16)
        if pos_bytes != count * 12 or typ_bytes != count * 4:
            continue
        f.seek(base + typ_off)
        types = struct.unpack(f"<{count}I", f.read(count * 4))
        hits = [i for i, ty_ in enumerate(types) if ty_ & 0xFFFF in sign]
        if not hits:
            continue
        f.seek(base + pos_off)
        pos = struct.unpack(f"<{count * 3}f", f.read(count * 12))
        far = max(max(abs(pos[3 * i]), abs(pos[3 * i + 2])) for i in hits)
        print(f"node {k} t=({tx:.0f},{tz:.0f}) block {count} inst, {len(hits)} signs, max |local| {far:.0f} m, e.g. {names[types[hits[0]] & 0xFFFF]} at ({tx + pos[3 * hits[0]]:.0f},{tz + pos[3 * hits[0] + 2]:.0f})")
