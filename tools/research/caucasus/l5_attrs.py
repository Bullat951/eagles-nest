# Attribute names (u32 1, u32 len, name, u16 type, u16 size, u32 flag, u64 bytes, u48 offset)
# around records of a shader in a surface5 entry stream; overall attribute-name counts.
import re, struct, sys
from collections import Counter

path, shader, show = sys.argv[1], sys.argv[2].encode(), int(sys.argv[3])
with open(path, "rb") as f:
    head = f.read(16)
    base = struct.unpack_from("<Q", head, 8)[0]
    f.seek(0)
    d = f.read(base)

attr = re.compile(rb"\x01\x00\x00\x00([\x01-\x20])\x00\x00\x00([A-Za-z_][A-Za-z0-9_]*)")
names = Counter()
for m in attr.finditer(d):
    if len(m.group(2)) == m.group(1)[0]:
        names[m.group(2).decode()] += 1
print("attribute names:", names.most_common(25))

rec = re.compile(rb"\x02\x00\x00\x00([\x05-\x20])\x00\x00\x00([A-Za-z]+[0-9.]*[0-9])")
recs = [(m.start(), m.group(2).decode()) for m in rec.finditer(d) if len(m.group(2)) == m.group(1)[0]]
hits = [i for i, (p, s) in enumerate(recs) if s == shader.decode()]
print(f"{len(recs)} records, {len(hits)} '{shader.decode()}'")
for i in hits[:show]:
    for j in (i - 1, i, i + 1):
        p, s = recs[j]
        end = recs[j + 1][0] if j + 1 < len(recs) else len(d)
        region = d[p:end]
        cn = struct.unpack_from("<I", d, p + 8 + len(s))[0]
        cls = d[p + 12 + len(s):p + 12 + len(s) + cn].decode(errors="replace")
        found = []
        for m in attr.finditer(region):
            n = m.group(1)[0]
            if len(m.group(2)) != n:
                continue
            o = m.end()
            typ, size, flag = struct.unpack_from("<HHI", region, o)
            nbytes = struct.unpack_from("<Q", region, o + 8)[0]
            off = int.from_bytes(region[o + 16:o + 22], "little")
            found.append(f"{m.group(2).decode()}(t{typ} s{size} f{flag} {nbytes}B @{off})")
        ident = region.find(b"\x00\x00\x80\x3f" + b"\x00" * 16 + b"\x00\x00\x80\x3f" + b"\x00" * 16 + b"\x00\x00\x80\x3f")
        mark = "  <--" if j == i else ""
        print(f"  rec {j} @{p} len {end - p} {s} / {cls}: {', '.join(found) or '-'}{' matrix@' + str(ident) if ident >= 0 else ''}{mark}")
    print()
