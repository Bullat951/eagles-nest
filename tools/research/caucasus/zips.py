import sys, zipfile, collections, struct
for zp in sys.argv[1:]:
    z = zipfile.ZipFile(zp)
    inf = z.infolist()
    print(f"== {zp.split(chr(92))[-1]}: {len(inf)} entries")
    print("   ", [i.filename for i in inf[:14]])
    d = z.read(inf[0].filename)
    if d[:4] == b"DDS ":
        h, w = struct.unpack_from("<II", d, 12); print(f"    first: {w}x{h} {d[84:88]}")
