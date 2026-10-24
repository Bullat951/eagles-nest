# Per airfield: the pixel offset (dP, dQ) at which DCS's runway centre line is brightest against
# the ground beside it in the land_39 texture. Pixel P = x/S + dP (along x north), Q = z/S + dQ.
import json, math, struct, sys, zipfile

zp, fields_json = sys.argv[1], sys.argv[2]
S = 10000 / 256
SP = 38.45
CP = -320000 / S - 125 + 320000 / SP
CQ = 636000 / S + 288 - 636000 / SP
GUESS = (0, 0)
SEARCH = 40
z = zipfile.ZipFile(zp)
names = set(z.namelist())
cache = {}


def c565(c):
    return ((c >> 11 & 31) * 255 // 31, (c >> 5 & 63) * 255 // 63, (c & 31) * 255 // 31)


def tile(i, j):
    key = (i, j)
    if key in cache:
        return cache[key]
    name = f"land_39_{i}_{j}.png.dds"
    if name not in names:
        cache[key] = None
        return None
    d = z.read(name)
    lum = bytearray(256 * 256)
    for by in range(64):
        for bx in range(64):
            o = 128 + (by * 64 + bx) * 16 + 8
            c0, c1 = struct.unpack_from("<HH", d, o)
            p0, p1 = c565(c0), c565(c1)
            cols = [p0, p1, tuple((2 * a + b) // 3 for a, b in zip(p0, p1)), tuple((a + 2 * b) // 3 for a, b in zip(p0, p1))]
            ls = [(r * 3 + g * 6 + b) // 10 for r, g, b in cols]
            bits = struct.unpack_from("<I", d, o + 4)[0]
            for k in range(16):
                lum[(by * 4 + k // 4) * 256 + bx * 4 + k % 4] = ls[bits >> 2 * k & 3]
    cache[key] = lum
    return lum


def px(P, Q):
    # P along x (pixel column within tile), Q along z (row).
    i, u = divmod(int(math.floor(P)), 256)
    j, v = divmod(int(math.floor(Q)), 256)
    t = tile(i, j)
    return None if t is None else t[v * 256 + u]


fields = json.load(open(fields_json, encoding="utf-8-sig"))
results = []
for f in fields:
    for rw in f["runways"][:1]:
        hdg = (-rw["course"]) % (2 * math.pi)  # DCS course is the negative heading
        ux, uz = math.cos(hdg), math.sin(hdg)  # along the runway (x north, z east)
        L = rw["length"]
        along = [(k / 20 - 0.5) * L * 0.9 for k in range(21)]
        best = None
        for dP in range(GUESS[0] - SEARCH, GUESS[0] + SEARCH + 1):
            for dQ in range(GUESS[1] - SEARCH, GUESS[1] + SEARCH + 1, 1):
                on = off = 0.0
                n = 0
                for a in along:
                    x, zz = rw["x"] + ux * a, rw["z"] + uz * a
                    c = px(x / SP + CP + dP, zz / SP + CQ + dQ)
                    s1 = px((x - uz * 150) / SP + CP + dP, (zz + ux * 150) / SP + CQ + dQ)
                    s2 = px((x + uz * 150) / SP + CP + dP, (zz - ux * 150) / SP + CQ + dQ)
                    if c is None or s1 is None or s2 is None:
                        continue
                    on += c
                    off += (s1 + s2) / 2
                    n += 1
                if n < 15:
                    continue
                score = (on - off) / n
                if best is None or score > best[0]:
                    best = (score, dP, dQ)
        if best:
            results.append((f["name"], best))
            print(f"{f['name']:24} contrast {best[0]:6.1f}  dP {best[1]:5}  dQ {best[2]:5}  x {rw['x']:.0f} z {rw['z']:.0f}", flush=True)
ps = sorted(r[1][1] for r in results); qs = sorted(r[1][2] for r in results)
print(f"median dP {ps[len(ps) // 2]}  dQ {qs[len(qs) // 2]}")


