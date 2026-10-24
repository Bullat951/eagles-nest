import re, sys, zipfile, struct, collections
z = zipfile.ZipFile(sys.argv[1])
idx = []
for i in z.infolist():
    m = re.match(r"land_(\d+)_(-?\d+)_(-?\d+)\.png\.dds$", i.filename)
    if m: idx.append((int(m[1]), int(m[2]), int(m[3])))
lv = collections.Counter(a for a, b, c in idx)
print("prefix numbers:", lv)
for L in lv:
    a = [x for l, x, y in idx if l == L]; b = [y for l, x, y in idx if l == L]
    print(f"  {L}: i {min(a)}..{max(a)}  j {min(b)}..{max(b)}  count {len(a)}")
d = z.read("land_156_-10_-1.png.dds")
h, w, mips = struct.unpack_from("<II", d, 12) + (struct.unpack_from("<I", d, 28)[0],)
print(f"tile: {w}x{h}, mips {mips}, fourCC {d[84:88]}, size {len(d)}")
