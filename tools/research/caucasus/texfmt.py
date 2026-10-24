import sys, zipfile, struct, collections
v = sys.argv[1]
want = {"HardSplatSummer.zip": ["village_1", "town_1", "hardsplatcolor.default"], "VectorDataAssetSummer.zip": ["field_1_mask", "for_field_overlay", "forestborder", "road_cauc_all", "rivers_caucasus_01", "senakiairfield", "airfield_onlay"],
        "CloseupTexturesSummer.zip": ["noise1", "for_field_1", "closeuptextures.default"], "LandNoiseSummer.zip": ["landnoise"], "NoiseSummer.zip": ["noise_1", "noise_2"]}
for zn, keys in want.items():
    z = zipfile.ZipFile(v + "\\" + zn)
    for n in z.namelist():
        if not n.endswith(".dds") or not any(k in n.lower() for k in keys):
            continue
        with z.open(n) as f:
            d = f.read(160)
        h, w = struct.unpack_from("<II", d, 12); mips = struct.unpack_from("<I", d, 28)[0]
        flags = struct.unpack_from("<I", d, 80)[0]; four = d[84:88]; bits = struct.unpack_from("<I", d, 88)[0]
        dx = struct.unpack_from("<I", d, 128)[0] if four == b"DX10" else None
        print(f"{zn:28} {n:42} {w}x{h} mips {mips} {four} bits {bits} pf 0x{flags:x} dxgi {dx}")
