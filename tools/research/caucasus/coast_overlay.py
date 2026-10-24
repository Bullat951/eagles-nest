# Draws DCS's shoreline (terrain cache water cells, 500 m) over decoded land_39 tiles for an offset.
# Image: columns = +x (north) to the right, rows = +z (east) down (the tiles' own layout).
# usage: coast_overlay.py <terrain.bin> <zip> <x0,z0,x1,z1 km> <dP> <dQ> <out.png>
import math, struct, sys, zipfile, zlib

tb, zp, reg, dP, dQ, out = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4]), int(sys.argv[5]), sys.argv[6]
import os
S = float(os.environ.get("LAND_S", 10000 / 256))
raw = open(tb, "rb").read()
gx0, gz0, step = struct.unpack_from("<3d", raw, 8)
nx, nz = struct.unpack_from("<2i", raw, 32)
surf = raw[104 + nx * nz * 2:104 + nx * nz * 3]
rx0, rz0, rx1, rz1 = (float(v) * 1000 for v in reg.split(","))
P0, P1 = int(rx0 / S + dP), int(rx1 / S + dP)
Q0, Q1 = int(rz0 / S + dQ), int(rz1 / S + dQ)
W, H = P1 - P0, Q1 - Q0
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
                        q = ((Q - Q0) * W + (P - P0)) * 3
                        img[q:q + 3] = bytes(cols[bits >> 2 * k & 3])

# DCS shoreline: cell edges between water and land, drawn as red points every ~10 m.
water = lambda i, j: surf[i * nz + j] in (2, 3)
for i in range(max(1, int((rx0 - gx0) / step)), min(nx - 1, int((rx1 - gx0) / step))):
    for j in range(max(1, int((rz0 - gz0) / step)), min(nz - 1, int((rz1 - gz0) / step))):
        w = water(i, j)
        for a, b in ((1, 0), (0, 1)):
            if water(i + a, j + b) == w:
                continue
            # boundary between cell (i,j) and its neighbour: a line at the shared edge
            for t in range(50):
                f = t / 50
                if a:  # neighbour north: edge at x = i+1, along z
                    x, zz = gx0 + (i + 1) * step, gz0 + (j + f) * step
                else:
                    x, zz = gx0 + (i + f) * step, gz0 + (j + 1) * step
                P, Q = int(x / S + dP), int(zz / S + dQ)
                if P0 <= P < P1 and Q0 <= Q < Q1:
                    q = ((Q - Q0) * W + (P - P0)) * 3
                    img[q:q + 3] = b"\xff\x20\x20"

import json, os
af = os.path.join(os.path.dirname(out), "cauc_airfields.json")
for a in json.load(open(af, encoding="utf-8-sig")):
    for rw in a["runways"]:
        h = (-rw["course"]) % (2 * math.pi)
        for t in range(400):
            s = (t / 399 - 0.5) * rw["length"]
            x, zz = rw["x"] + math.cos(h) * s, rw["z"] + math.sin(h) * s
            P, Q = int(x / S + dP), int(zz / S + dQ)
            for dd2 in (-1, 0, 1):
                if P0 <= P < P1 and Q0 <= Q + dd2 < Q1:
                    q = ((Q + dd2 - Q0) * W + (P - P0)) * 3
                    img[q:q + 3] = b"\xff\xff\x00"
rows = b"".join(b"\x00" + bytes(img[y * W * 3:(y + 1) * W * 3]) for y in range(H))
ch = lambda t, dd: struct.pack(">I", len(dd)) + t + dd + struct.pack(">I", zlib.crc32(t + dd) & 0xFFFFFFFF)
open(out, "wb").write(b"\x89PNG\r\n\x1a\n" + ch(b"IHDR", struct.pack(">IIBBBBB", W, H, 8, 2, 0, 0, 0)) + ch(b"IDAT", zlib.compress(rows, 6)) + ch(b"IEND", b""))
print(f"wrote {out} {W}x{H}")


