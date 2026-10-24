import re, struct, sys
path = sys.argv[1]
f = open(path, "rb"); base = struct.unpack_from("<Q", f.read(16), 8)[0]; f.seek(0); d = f.read(base)
for label, a in (("field material", 13318440), ("forest material", 13317400), ("town materials", 13316900), ("surface_tiling", 13309990)):
    blk = d[a:a + 420]
    print(f"== {label} @{a}")
    i = 0
    while i < len(blk):
        m = re.match(rb"[ -~]{3,}", blk[i:])
        if m:
            print(f"   +{i:3} '{m.group().decode()}'"); i += len(m.group()); continue
        if i + 4 <= len(blk):
            fv = struct.unpack_from("<f", blk, i)[0]; iv = struct.unpack_from("<i", blk, i)[0]
            if 1e-4 < abs(fv) < 1e6 and fv == fv:
                print(f"   +{i:3} float {fv:.6g}  (int {iv})")
            elif 0 < iv < 100000:
                print(f"   +{i:3} int {iv}")
        i += 4 if i % 4 == 0 else 1
