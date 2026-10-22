"""Probe for DCS terrain surface files (Mods/terrains/<id>/surface/<id>.surface5,
"landscape5::Surface5File"), reverse engineered from the user's own install for local use.
Format notes in docs/DCS_MAP_FORMAT.md, "Terrain surface"; the extractor the client uses
is client/Dcb.Client.Core/LandCover.cs. Reads the first 64 MB of the entry stream and lists
the records and their attributes; counts per (shader, class). Remember the one-off shift:
the attributes listed under a record belong to the next record's class.

usage: python -I dcs_surface5_probe.py <terrain.surface5> [class]
   class: print the attributes of the first few records of that class (e.g. Town)
"""
import collections
import re
import struct
import sys

SHADERS = [b'Surface5.0', b'HardSplat5.2', b'Splatmap5.1', b'Simple5.1', b'Sea5.1', b'Lake5.1', b'MeshAssetManifold5']
ATTR = re.compile(rb'\x01\x00\x00\x00(.)\x00\x00\x00([A-Za-z]{3,24})', re.S)


def records(d):
    """(offset, shader, class) per record: u32 2, length-prefixed shader, length-prefixed class."""
    out = []
    for sh in SHADERS:
        pat = struct.pack('<II', 2, len(sh)) + sh
        for m in re.finditer(re.escape(pat), d):
            o = m.start() + len(pat)
            n = struct.unpack_from('<I', d, o)[0]
            if 0 < n < 40:
                out.append((m.start(), sh.decode(), d[o + 4:o + 4 + n].decode(errors='replace')))
    return sorted(out)


def attrs(d, start, end):
    """name -> (type, element size, flag, byte count, data offset) between start and end.
    Counts are in bytes; the 48-bit offset is relative to the data base (header u64 at 8)."""
    res = {}
    for m in ATTR.finditer(d, start, end):
        name = m.group(2).decode()
        if len(name) != m.group(1)[0]:
            continue
        o = m.end()
        t, sz, flag = struct.unpack_from('<HHI', d, o)
        cnt = struct.unpack_from('<Q', d, o + 8)[0]
        off = int.from_bytes(d[o + 16:o + 22], 'little')
        res[name] = (t, sz, flag, cnt, off)
    return res


def main():
    with open(sys.argv[1], 'rb') as f:
        d = f.read(64 << 20)
    base = struct.unpack_from('<Q', d, 8)[0]
    recs = records(d)
    print('data base', base, '-', len(recs), 'records in the first 64 MB')
    for (sh, cls), n in collections.Counter((s, c) for _, s, c in recs).most_common():
        print('  %-20s %-10s %6d' % (sh, cls, n))
    if len(sys.argv) > 2:
        starts = [r[0] for r in recs] + [len(d)]
        shown = 0
        for k, (o, sh, cls) in enumerate(recs):
            if cls != sys.argv[2]:
                continue
            a = attrs(d, o, starts[k + 1])
            print('\n%s record @%x:' % (cls, o))
            for name, (t, sz, flag, cnt, off) in a.items():
                print('  %-20s type %d size %2d flag %d  %8d bytes (%d elements) @ data+%x' % (name, t, sz, flag, cnt, cnt // max(1, sz), off))
            shown += 1
            if shown >= 3:
                break


if __name__ == '__main__':
    main()
