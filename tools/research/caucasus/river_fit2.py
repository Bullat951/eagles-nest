# Registers the land_39 texture to DCS by its rivers: DCS's F10 "River" lines (sup5) against
# the texture's blueness. Pixel P = x/S + dP (column, along x north), Q = z/S + dQ (row, z east).
# usage: river_fit.py <sup5> <LandTextures zip> <x0,z0,x1,z1 region km> [...more regions]
import math, struct, sys, zipfile

sup5, zp, regions = sys.argv[1], sys.argv[2], sys.argv[3:]
S = 38.4
GUESS = (0, 0)

d = open(sup5, "rb").read()
base = struct.unpack_from("<Q", d, 8)[0]
table = d[:base]
one = b"\x00\x00\x80\x3f"
node_pat = one + b"\x00" * 16 + one + b"\x00" * 16 + one + b"\x00" * 4
verts_pat = bytes([1, 0, 0, 0, 1, 0, 0, 0, 0x50, 0x02, 0x00, 0x0C, 0, 0, 0, 0, 0])
geom_pat = bytes([8, 0, 0, 0]) + b"LINELIST"


def find_all(pat):
    out, at = [], 0
    while True:
        i = table.find(pat, at)
        if i < 0:
            return out
        out.append(i)
        at = i + len(pat)


import bisect
nodes, verts, geoms = find_all(node_pat), find_all(verts_pat), find_all(geom_pat)
segs = []
for g in geoms:
    n = struct.unpack_from("<i", d, g + 52)[0]
    if not 1 <= n <= 64 or d[g + 56:g + 56 + n] != b"River":
        continue
    ib_bytes, ib_off = struct.unpack_from("<QQ", d, g + 20)
    vi = bisect.bisect_left(verts, g) - 1
    if vi < 0:
        continue
    v = verts[vi] + len(verts_pat)
    vb_bytes = struct.unpack_from("<Q", d, v)[0]
    vb_off = struct.unpack_from("<I", d, v + 8)[0]
    ni = bisect.bisect_left(nodes, verts[vi]) - 1
    tx = tz = 0.0
    if ni >= 0:
        tx, tz = struct.unpack_from("<f", d, nodes[ni] + 48)[0], struct.unpack_from("<f", d, nodes[ni] + 56)[0]
    nv = vb_bytes // 12
    for k in range(0, ib_bytes - 7, 8):
        a, b = struct.unpack_from("<ii", d, base + ib_off + k)
        if not (0 <= a < nv and 0 <= b < nv):
            continue
        ax, _, az = struct.unpack_from("<3f", d, base + vb_off + a * 12)
        bx, _, bz = struct.unpack_from("<3f", d, base + vb_off + b * 12)
        segs.append((ax + tx, az + tz, bx + tx, bz + tz))
print(f"river segments: {len(segs)}")

z = zipfile.ZipFile(zp)
names = set(z.namelist())
cache = {}


def c565(c):
    return ((c >> 11 & 31) * 255 // 31, (c >> 5 & 63) * 255 // 63, (c & 31) * 255 // 31)


def tile(i, j):
    if (i, j) in cache:
        return cache[(i, j)]
    name = f"land_39_{i}_{j}.png.dds"
    if name not in names:
        cache[(i, j)] = None
        return None
    dd = z.read(name)
    w = bytearray(256 * 256)
    for by in range(64):
        for bx in range(64):
            o = 128 + (by * 64 + bx) * 16 + 8
            c0, c1 = struct.unpack_from("<HH", dd, o)
            p0, p1 = c565(c0), c565(c1)
            cols = [p0, p1, tuple((2 * a + b) // 3 for a, b in zip(p0, p1)), tuple((a + 2 * b) // 3 for a, b in zip(p0, p1))]
            ws = [max(0, min(255, 128 + b - r)) for r, g, b in cols]   # blueness
            bits = struct.unpack_from("<I", dd, o + 4)[0]
            for k in range(16):
                w[(by * 4 + k // 4) * 256 + bx * 4 + k % 4] = ws[bits >> 2 * k & 3]
    cache[(i, j)] = w
    return w


def raw(P, Q):
    t = tile(P >> 8, Q >> 8)
    return None if t is None else t[(Q & 255) * 256 + (P & 255)]


def val(P, Q):
    # pixel centres at +0.5
    P -= 0.5; Q -= 0.5
    p0, q0 = int(math.floor(P)), int(math.floor(Q))
    fp, fq = P - p0, Q - q0
    a, b, c, d = raw(p0, q0), raw(p0 + 1, q0), raw(p0, q0 + 1), raw(p0 + 1, q0 + 1)
    if None in (a, b, c, d):
        return None
    return (a * (1 - fp) + b * fp) * (1 - fq) + (c * (1 - fp) + d * fp) * fq


for reg in regions:
    x0, z0, x1, z1 = (float(v) * 1000 for v in reg.split(","))
    pts = []
    for ax, az, bx, bz in segs:
        if not (x0 <= ax <= x1 and z0 <= az <= z1):
            continue
        L = math.hypot(bx - ax, bz - az)
        for k in range(max(1, int(L / 40))):
            t = k / max(1, int(L / 40))
            pts.append((ax + (bx - ax) * t, az + (bz - az) * t))
    base_pts = [(x / S, zz / S) for x, zz in pts]

    def score(dP, dQ):
        s = n = 0
        for P, Q in base_pts:
            v = val(P + dP, Q + dQ)
            if v is not None:
                s += v
                n += 1
        return s / n if n else 0

    best = max(((score(dP, dQ), dP, dQ) for dP in range(-10, 11) for dQ in range(-10, 11)))
    for st in (0.5, 0.25, 0.125):
        c = best
        best = max(((score(c[1] + a * st, c[2] + b * st), c[1] + a * st, c[2] + b * st) for a in (-2, -1, 0, 1, 2) for b in (-2, -1, 0, 1, 2)))
    ring = sum(score(best[1] + a, best[2] + b) for a, b in ((2, 0), (-2, 0), (0, 2), (0, -2))) / 4
    print(f"region {reg}: {len(pts)} river points, best dP {best[1]} dQ {best[2]} score {best[0]:.1f} (2 px away {ring:.1f})", flush=True)

