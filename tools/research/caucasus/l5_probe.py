# landscape5 probe: header, (shader, class) record counts of a surface5, sign-like model names of a scn5.
import re, struct, sys
from collections import Counter

path, kind = sys.argv[1], sys.argv[2]
with open(path, "rb") as f:
    head = f.read(16)
    base = struct.unpack_from("<Q", head, 8)[0]
    print(f"{path}: magic {head[:8]!r} data base {base:,}")
    d = f.read(base - 16) if kind == "scn" or base < 400_000_000 else None
    if d is None:
        f.seek(0)
        d = f.read(base)
    else:
        d = head + d

if kind == "surface":
    c = Counter()
    for m in re.finditer(rb"\x02\x00\x00\x00([\x05-\x20])\x00\x00\x00", d):
        n = m.group(1)[0]
        p = m.end()
        sh = d[p:p + n]
        if not re.fullmatch(rb"[A-Za-z]+[0-9.]*[0-9]", sh):
            continue
        q = p + n
        if q + 4 > len(d):
            continue
        cn = struct.unpack_from("<I", d, q)[0]
        if not 0 < cn < 40:
            continue
        cl = d[q + 4:q + 4 + cn]
        if not re.fullmatch(rb"[A-Za-z_]+", cl):
            continue
        c[(sh.decode(), cl.decode())] += 1
    for (sh, cl), k in sorted(c.items(), key=lambda kv: -kv[1])[:60]:
        print(f"  {k:8} {sh:22} {cl}")
    for pat in (b"Runway", b"runway", b"Airfield", b"airfield", b"Concrete", b"concrete", b"Asphalt", b"asphalt"):
        print(f"  '{pat.decode()}' occurrences: {d.count(pat)}")
else:
    names, o = [], 66
    while o + 4 <= len(d):
        n = struct.unpack_from("<I", d, o)[0]
        if not 1 <= n <= 80:
            break
        s = d[o + 4:o + 4 + n]
        if any(ch < 0x20 or ch > 0x7E for ch in s):
            break
        names.append(s.decode())
        o += 4 + n
    print(f"  model names from 66: {len(names)}; first {names[:5]}")
    keys = ("black", "yellow", "course", "sign", "tab", "kdp", "tower", "arrow")
    hits = [n for n in names if any(k in n.lower() for k in keys)]
    print(f"  sign-like: {len(hits)}: {hits[:60]}")
    if not names:
        # find where a string table might be: first runs of printable length-prefixed strings
        for m in re.finditer(rb"[\x03-\x40]\x00\x00\x00[a-zA-Z_][a-zA-Z0-9_\-\.]{2,63}", d[:5_000_000]):
            print("  first string-like at", m.start(), d[m.start() + 4:m.start() + 40])
            break
    print(f"  'TYPE_OPTIONS': {d.count(b'TYPE_OPTIONS')}, block tail: {d.count(bytes([4,0,0,0,1,0,0,0,1,0,0,0,0x50,2,0,0x0c]))}")
