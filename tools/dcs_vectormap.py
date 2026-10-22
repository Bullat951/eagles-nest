"""Decoder for DCS World's F10 vector map (Mods/terrains/<id>/Map/<id>.sup5), reverse
engineered from the user's own install for local use. Format notes: docs/DCS_MAP_FORMAT.md.

The file is a landscape5 "sup5" scene: a 48-byte header (uint64 at offset 8 = data-block
base), then a stream of entries, then the data block (index and vertex buffers). Three
entry kinds matter, read in file order:

  node      4x4 float matrix (identity rotation, translation at elements 12..14 = tile
            origin on a 16384 m grid) + 6-float local bbox. Sets the tile offset.
  vertices  u32 1, u32 1, "50 02 00 0c", 5 bytes 0, u64 vertex_bytes, u32 vertex_offset:
            float32 (x, y, z) triples, local to the current tile.
  geometry  u32 8, "LINELIST", u16 fmt, u16 stride, u32 1, u64 index_bytes,
            u64 index_offset, u16 0, u32 n, strings (shader "Map4.0", class "Road" ...),
            params ("colorIndex", "shaderDefine"). Its u32 index pairs refer to the most
            recent vertices entry before it.

Offsets are relative to the data-block base. x is north, z east (DCS map metres).

usage: python -I dcs_vectormap.py <file.sup5> [out.json]
Prints per-class statistics; with out.json writes {"class": [[x0,z0,x1,z1,...], ...]}.
"""
import bisect
import collections
import json
import re
import struct
import sys

ONE = b"\x00\x00\x80\x3f"
NODE = re.compile(re.escape(ONE + b"\x00" * 16 + ONE + b"\x00" * 16 + ONE + b"\x00" * 4))
VERTS = re.compile(re.escape(b"\x01\x00\x00\x00\x01\x00\x00\x00\x50\x02\x00\x0c" + b"\x00" * 5))
GEOM = re.compile(rb"\x08\x00\x00\x00LINELIST")
NAME = re.compile(rb"(?s)(.{4})([A-Za-z_][\x20-\x7e]{0,62})")


def parse(path):
    data = open(path, "rb").read()
    base = struct.unpack_from("<Q", data, 8)[0]
    table_end = base  # entries live before the data block

    nodes = [(m.start(), struct.unpack_from("<3f", data, m.start() + 48)) for m in NODE.finditer(data, 0, table_end)]
    node_pos = [p for p, _ in nodes]
    verts = []
    for m in VERTS.finditer(data, 0, table_end):
        vb_bytes = struct.unpack_from("<Q", data, m.end())[0]
        vb_off = struct.unpack_from("<I", data, m.end() + 8)[0]
        if 0 < vb_bytes < 50_000_000 and base + vb_off + vb_bytes <= len(data):
            verts.append((m.start(), vb_bytes, vb_off))
    vert_pos = [p for p, _, _ in verts]

    out = collections.defaultdict(list)
    skipped = 0
    for m in GEOM.finditer(data, 0, table_end):
        g = m.start()
        ib_bytes, ib_off = struct.unpack_from("<QQ", data, g + 20)
        strings = [s.group(2).decode() for s in NAME.finditer(data, g + 38, g + 160)
                   if struct.unpack("<i", s.group(1))[0] == len(s.group(2))]
        cls = strings[1] if len(strings) > 1 else "?"
        vi = bisect.bisect_left(vert_pos, g) - 1
        if vi < 0 or not ib_bytes or base + ib_off + ib_bytes > len(data):
            skipped += 1
            continue
        vpos, vb_bytes, vb_off = verts[vi]
        ni = bisect.bisect_left(node_pos, vpos) - 1
        tx, _, tz = nodes[ni][1] if ni >= 0 else (0.0, 0.0, 0.0)
        n = vb_bytes // 12
        v = struct.unpack_from(f"<{n * 3}f", data, base + vb_off)
        idx = struct.unpack_from(f"<{ib_bytes // 4}I", data, base + ib_off)
        segs = []
        for a, b in zip(idx[0::2], idx[1::2]):
            if a < n and b < n:
                segs += [v[3 * a] + tx, v[3 * a + 2] + tz, v[3 * b] + tx, v[3 * b + 2] + tz]
        if segs:
            out[cls].append(segs)
    return out, len(nodes), len(verts), skipped


def main():
    lines, nnodes, nverts, skipped = parse(sys.argv[1])
    print(f"nodes={nnodes} vertex buffers={nverts} skipped geometries={skipped}")
    for cls, groups in sorted(lines.items(), key=lambda kv: -len(kv[1])):
        xs = [v for g in groups for v in g[0::2]]
        zs = [v for g in groups for v in g[1::2]]
        print(f"{cls:10s} groups={len(groups):6d} segments={sum(len(g) // 4 for g in groups):8d} "
              f"x {min(xs):10.0f}..{max(xs):10.0f} z {min(zs):10.0f}..{max(zs):10.0f}")
    if len(sys.argv) > 2:
        with open(sys.argv[2], "w") as f:
            json.dump({c: [[round(v, 1) for v in g] for g in gs] for c, gs in lines.items()}, f, separators=(",", ":"))


if __name__ == "__main__":
    main()
