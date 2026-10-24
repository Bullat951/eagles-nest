# Decode DCS's DXT5 map palette and print the class swatches (right half) and ramp ends.
import struct, sys, zipfile

with zipfile.ZipFile(sys.argv[1]) as z:
    dds = z.read("mapmodepalette.tif.dds")
h, w = struct.unpack_from("<II", dds, 12)
bw = (w + 3) // 4
px = {}


def c565(c):
    return ((c >> 11 & 31) * 255 // 31, (c >> 5 & 63) * 255 // 63, (c & 31) * 255 // 31)


for by in range((h + 3) // 4):
    for bx in range(bw):
        o = 128 + (by * bw + bx) * 16
        a0, a1 = dds[o], dds[o + 1]
        al = [a0, a1] + ([((8 - i) * a0 + (i - 1) * a1) // 7 for i in range(2, 8)] if a0 > a1
                         else [((6 - i) * a0 + (i - 1) * a1) // 5 for i in range(2, 6)] + [0, 255])
        abits = int.from_bytes(dds[o + 2:o + 8], "little")
        r0, r1 = c565(dds[o + 8] | dds[o + 9] << 8), c565(dds[o + 10] | dds[o + 11] << 8)
        cols = [r0, r1, tuple((2 * a + b) // 3 for a, b in zip(r0, r1)), tuple((a + 2 * b) // 3 for a, b in zip(r0, r1))]
        cbits = struct.unpack_from("<I", dds, o + 12)[0]
        for i in range(16):
            px[(bx * 4 + i % 4, by * 4 + i // 4)] = cols[cbits >> 2 * i & 3] + (al[abits >> 3 * i & 7],)

print(f"{w}x{h}; ramp top {px[(w // 4, 0)]}, bottom {px[(w // 4, h - 1)]}")
sw = h // 16
for k in range(16):
    print(f"class {k:2}: {px[(3 * w // 4, k * sw + sw // 2)]}")
