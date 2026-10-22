"""Builds the client's offline scope base map from Natural Earth 1:10m shapefiles.

Keeps only the parts of coastlines, land borders and lake shores that fall inside the
DCS theatres, rounds to 4 decimals (~10 m) and writes compact JSON:
  {"coast": [[lon, lat, lon, lat, ...], ...], "borders": [...], "lakes": [...]}

usage: python -I make_basemap.py <naturalearth dir> <output json>
"""
import json
import struct
import sys
from pathlib import Path

# (lat_min, lat_max, lon_min, lon_max), generous margins around each DCS theatre.
THEATRES = {
    "Caucasus": (38.0, 48.0, 33.0, 49.0),
    "Syria": (29.0, 40.0, 27.0, 45.0),
    "PersianGulf": (21.0, 32.0, 47.0, 62.0),
    "Sinai": (25.0, 34.0, 27.0, 38.0),
    "Iraq": (27.0, 40.0, 36.0, 51.0),
    "Afghanistan": (27.0, 41.0, 58.0, 75.0),
    "Kola": (62.0, 72.0, 10.0, 45.0),
    "GermanyCW": (47.0, 57.0, 3.0, 18.0),
    "Normandy": (47.0, 53.0, -5.0, 5.0),
    "TheChannel": (48.0, 53.0, -3.0, 4.0),
    "MarianaIslands": (9.0, 24.0, 138.0, 152.0),
    "Nevada": (33.0, 40.0, -121.0, -111.0),
    "Falklands": (-58.0, -44.0, -78.0, -52.0),
}
SOURCES = {
    "coast": "ne_10m_coastline/ne_10m_coastline.shp",
    "borders": "ne_10m_admin_0_boundary_lines_land/ne_10m_admin_0_boundary_lines_land.shp",
    "lakes": "ne_10m_lakes/ne_10m_lakes.shp",
}


def read_parts(shp_path):
    """Yields each part of every polyline/polygon record as a list of (lon, lat)."""
    data = Path(shp_path).read_bytes()
    pos = 100
    while pos < len(data):
        _, length = struct.unpack(">ii", data[pos:pos + 8])
        rec = data[pos + 8:pos + 8 + length * 2]
        pos += 8 + length * 2
        shape_type = struct.unpack("<i", rec[:4])[0]
        if shape_type not in (3, 5, 13, 15, 23, 25):
            continue
        num_parts, num_points = struct.unpack("<ii", rec[36:44])
        parts = list(struct.unpack(f"<{num_parts}i", rec[44:44 + 4 * num_parts]))
        pts_off = 44 + 4 * num_parts
        coords = struct.unpack(f"<{2 * num_points}d", rec[pts_off:pts_off + 16 * num_points])
        parts.append(num_points)
        for a, b in zip(parts, parts[1:]):
            yield [(coords[2 * i], coords[2 * i + 1]) for i in range(a, b)]


def inside(lon, lat):
    return any(la0 <= lat <= la1 and lo0 <= lon <= lo1 for la0, la1, lo0, lo1 in THEATRES.values())


def clip(part):
    """Splits a line into runs of points inside any theatre, keeping one point either
    side so lines reach the theatre edge."""
    runs, run = [], []
    flags = [inside(lon, lat) for lon, lat in part]
    for i, pt in enumerate(part):
        near = flags[i] or (i > 0 and flags[i - 1]) or (i + 1 < len(part) and flags[i + 1])
        if near:
            run.append(pt)
        elif run:
            runs.append(run)
            run = []
    if run:
        runs.append(run)
    return [r for r in runs if len(r) >= 2]


def main():
    src, out = Path(sys.argv[1]), Path(sys.argv[2])
    result = {}
    for layer, rel in SOURCES.items():
        lines = []
        for part in read_parts(src / rel):
            for run in clip(part):
                flat = []
                for lon, lat in run:
                    flat += [round(lon, 4), round(lat, 4)]
                lines.append(flat)
        result[layer] = lines
        print(f"{layer}: {len(lines)} lines, {sum(len(l) for l in lines) // 2} points")
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(result, separators=(",", ":")), encoding="utf-8")
    print(f"wrote {out} ({out.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
