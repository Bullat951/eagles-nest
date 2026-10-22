"""Extract taxiway/runway centre lines from DCS airfield road networks
(Mods/terrains/<id>/AirfieldsTaxiways/<field>.rn4, "landscape4::lRoadNetwork"), reverse
engineered from the user's own install for local use. Format notes: docs/DCS_MAP_FORMAT.md.

Each edge is stored as a header ending in u32 point_count, followed by point_count
(x, y, z) float64 triples in DCS map metres (x north, z east, y = elevation). The extractor
matches "count + that many plausible, closely spaced points".

usage: python -I dcs_taxiways.py <field.rn4> [out.json]
"""
import json
import struct
import sys


def polylines(data):
    out = []
    o = 0
    n_bytes = len(data)
    while o + 4 + 48 <= n_bytes:
        n = struct.unpack_from("<I", data, o)[0]
        if 2 <= n <= 5000 and o + 4 + 24 * n <= n_bytes:
            pts = []
            ok = True
            px = pz = None
            for k in range(n):
                x, y, z = struct.unpack_from("<3d", data, o + 4 + 24 * k)
                if not (-2e6 < x < 2e6 and -2e6 < z < 2e6 and -500 < y < 6000) or (x == 0 and z == 0):
                    ok = False
                    break
                if px is not None and (abs(x - px) > 3000 or abs(z - pz) > 3000):
                    ok = False
                    break
                pts.append((x, z))
                px, pz = x, z
            if ok:
                out.append(pts)
                o += 4 + 24 * n
                continue
        o += 1
    return out


def near_field(lines, radius=20000, local=3000):
    """The file also holds shapes in local coordinates around (0, 0) (stand/marking
    templates) and the odd byte pattern that looks like points. When the field itself is
    away from the map origin, drop polylines lying entirely within `local` of the origin,
    then keep those within `radius` of the median of what is left."""
    far = [l for l in lines if any(abs(p[0]) > local or abs(p[1]) > local for p in l)]
    if far:
        lines = far
    xs = sorted(p[0] for l in lines for p in l)
    zs = sorted(p[1] for l in lines for p in l)
    mx, mz = xs[len(xs) // 2], zs[len(zs) // 2]
    return [l for l in lines if all(abs(p[0] - mx) < radius and abs(p[1] - mz) < radius for p in l)]


def main():
    data = open(sys.argv[1], "rb").read()
    lines = near_field(polylines(data))
    xs = [p[0] for l in lines for p in l]
    zs = [p[1] for l in lines for p in l]
    print(f"{len(lines)} polylines, {len(xs)} points, x {min(xs):.0f}..{max(xs):.0f} z {min(zs):.0f}..{max(zs):.0f}")
    if len(sys.argv) > 2:
        flat = {"Taxi": [[v for p in l[:-1] for v in (p[0], p[1])] + [l[-1][0], l[-1][1]] for l in lines]}
        # render_lines.py expects segment lists x0,z0,x1,z1,...
        segs = {"Road": []}
        for l in lines:
            s = []
            for a, b in zip(l, l[1:]):
                s += [a[0], a[1], b[0], b[1]]
            segs["Road"].append(s)
        json.dump(segs, open(sys.argv[2], "w"))


if __name__ == "__main__":
    main()
