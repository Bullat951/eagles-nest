# DCS F10 river lines (sup5) and runway centre lines over the land_39 texture with the client's
# constants. Image: right = north (+x), down = east (+z), 2 px per texture pixel.
# usage: river_overlay.py <sup5> <zip> <airfields.json> <x0,z0,x1,z1 km> <out.png>
import bisect, json, math, struct, sys, zipfile, zlib

SX, SZ, OX, OZ = 38.398, 38.3929, -0.64, 1.10
sup5, zp, afj, reg, out = sys.argv[1:6]
K = 2
rx0, rz0, rx1, rz1 = (float(v) * 1000 for v in reg.split(","))
P0, P1 = int(rx0 / SX + OX), int(rx1 / SX + OX)
Q0, Q1 = int(rz0 / SZ + OZ), int(rz1 / SZ + OZ)
W, H = (P1 - P0) * K, (Q1 - Q0) * K
img = bytearray(W * H * 3)
z = zipfile.ZipFile(zp)
names = set(z.namelist())


def c565(c):
    return ((c >> 11 & 31) * 255 // 31, (c >> 5 & 63) * 255 // 63, (c & 31) * 255 // 31)


for ti in range(P0 >> 8, (P1 >> 8) + 1):
    for tj in range(Q0 >> 8, (Q1 >> 8) + 1):
        name = f"land_39_{ti}_{tj}.png.dds"
        if name not in names:
            continue
        dd = z.read(name)
        for by in range(64):
            for bx in range(64):
                o = 128 + (by * 64 + bx) * 16 + 8
                c0, c1 = struct.unpack_from("<HH", dd, o)
                p0, p1 = c565(c0), c565(c1)
                cols = [p0, p1, tuple((2 * a + b) // 3 for a, b in zip(p0, p1)), tuple((a + 2 * b) // 3 for a, b in zip(p0, p1))]
                bits = struct.unpack_from("<I", dd, o + 4)[0]
                for k in range(16):
                    P, Q = ti * 256 + bx * 4 + k % 4, tj * 256 + by * 4 + k // 4
                    if P0 <= P < P1 and Q0 <= Q < Q1:
                        for a in range(K):
                            for b in range(K):
                                q = (((Q - Q0) * K + b) * W + (P - P0) * K + a) * 3
                                img[q:q + 3] = bytes(cols[bits >> 2 * k & 3])


def dot(x, zz, col):
    u, v = (x / SX + OX - P0) * K, (zz / SZ + OZ - Q0) * K
    if 0 <= u < W and 0 <= v < H:
        q = (int(v) * W + int(u)) * 3
        img[q:q + 3] = col


d = open(sup5, "rb").read()
base = struct.unpack_from("<Q", d, 8)[0]
table = d[:base]
one = b"\x00\x00\x80\x3f"
pats = (one + b"\x00" * 16 + one + b"\x00" * 16 + one + b"\x00" * 4, bytes([1, 0, 0, 0, 1, 0, 0, 0, 0x50, 0x02, 0x00, 0x0C, 0, 0, 0, 0, 0]), bytes([8, 0, 0, 0]) + b"LINELIST")


def find_all(p):
    o, at = [], 0
    while (i := table.find(p, at)) >= 0:
        o.append(i)
        at = i + len(p)
    return o


nodes, verts, geoms = (find_all(p) for p in pats)
for g in geoms:
    n = struct.unpack_from("<i", d, g + 52)[0]
    if not 1 <= n <= 64 or d[g + 56:g + 56 + n] != b"River":
        continue
    ibb, ibo = struct.unpack_from("<QQ", d, g + 20)
    vi = bisect.bisect_left(verts, g) - 1
    v = verts[vi] + len(pats[1])
    vbb, vbo = struct.unpack_from("<Q", d, v)[0], struct.unpack_from("<I", d, v + 8)[0]
    ni = bisect.bisect_left(nodes, verts[vi]) - 1
    tx, tz = (struct.unpack_from("<f", d, nodes[ni] + 48)[0], struct.unpack_from("<f", d, nodes[ni] + 56)[0]) if ni >= 0 else (0, 0)
    nv = vbb // 12
    for k in range(0, ibb - 7, 8):
        a, b = struct.unpack_from("<ii", d, base + ibo + k)
        if not (0 <= a < nv and 0 <= b < nv):
            continue
        ax, _, az = struct.unpack_from("<3f", d, base + vbo + a * 12)
        bx, _, bz = struct.unpack_from("<3f", d, base + vbo + b * 12)
        ax += tx; az += tz; bx += tx; bz += tz
        if not (rx0 - 2000 < ax < rx1 + 2000 and rz0 - 2000 < az < rz1 + 2000):
            continue
        L = math.hypot(bx - ax, bz - az)
        for s in range(int(L / 5) + 1):
            f = s / max(1, int(L / 5))
            dot(ax + (bx - ax) * f, az + (bz - az) * f, b"\xff\x30\x30")
for a in json.load(open(afj, encoding="utf-8-sig")):
    for rw in a["runways"]:
        h = (-rw["course"]) % (2 * math.pi)
        for s in range(600):
            t = (s / 599 - 0.5) * rw["length"]
            dot(rw["x"] + math.cos(h) * t, rw["z"] + math.sin(h) * t, b"\xff\xff\x00")
rows = b"".join(b"\x00" + bytes(img[y * W * 3:(y + 1) * W * 3]) for y in range(H))
ch = lambda t, dd: struct.pack(">I", len(dd)) + t + dd + struct.pack(">I", zlib.crc32(t + dd) & 0xFFFFFFFF)
open(out, "wb").write(b"\x89PNG\r\n\x1a\n" + ch(b"IHDR", struct.pack(">IIBBBBB", W, H, 8, 2, 0, 0, 0)) + ch(b"IDAT", zlib.compress(rows, 6)) + ch(b"IEND", b""))
print(f"wrote {out} {W}x{H}")
