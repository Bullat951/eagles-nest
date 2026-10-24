# Mosaic of land_<lvl>_<a>_<b> tiles (DXT5 256x256) around index (a0, b0) into a PNG, stdlib only.
# usage: land_mosaic.py <zip> <lvl> <a0> <b0> <radius> <out.png> [swap]
# Without 'swap': tile a -> mosaic column (east), b -> row; with 'swap': a -> row, b -> column.
import struct, sys, zipfile, zlib

zp, lvl, a0, b0, r, out = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5]), sys.argv[6]
swap = len(sys.argv) > 7
z = zipfile.ZipFile(zp)
names = set(z.namelist())
T = 256
n = 2 * r + 1
img = bytearray(n * T * n * T * 3)


def c565(c):
    return ((c >> 11 & 31) * 255 // 31, (c >> 5 & 63) * 255 // 63, (c & 31) * 255 // 31)


def decode(dds):
    w, h = struct.unpack_from("<II", dds, 16)[0], struct.unpack_from("<I", dds, 12)[0]
    px = bytearray(w * h * 3)
    bw = w // 4
    for by in range(h // 4):
        for bx in range(bw):
            o = 128 + (by * bw + bx) * 16 + 8   # colour half of the DXT5 block
            c0, c1 = struct.unpack_from("<HH", dds, o)
            p0, p1 = c565(c0), c565(c1)
            cols = [p0, p1, tuple((2 * x + y) // 3 for x, y in zip(p0, p1)), tuple((x + 2 * y) // 3 for x, y in zip(p0, p1))]
            bits = struct.unpack_from("<I", dds, o + 4)[0]
            for k in range(16):
                x, y = bx * 4 + k % 4, by * 4 + k // 4
                q = (y * w + x) * 3
                px[q:q + 3] = bytes(cols[bits >> 2 * k & 3])
    return w, h, px


for da in range(-r, r + 1):
    for db in range(-r, r + 1):
        name = f"land_{lvl}_{a0 + da}_{b0 + db}.png.dds"
        if name not in names:
            continue
        w, h, px = decode(z.read(name))
        col, row = (db + r, da + r) if swap else (da + r, db + r)
        for y in range(T):
            dst = ((row * T + y) * n * T + col * T) * 3
            img[dst:dst + T * 3] = px[y * T * 3:(y + 1) * T * 3]
        # tile border and label: a thin red line at the tile's top and left
        for x in range(T):
            q = ((row * T) * n * T + col * T + x) * 3
            img[q:q + 3] = b"\xff\x00\x00"
            q = ((row * T + x) * n * T + col * T) * 3
            img[q:q + 3] = b"\xff\x00\x00"

W = H = n * T
raw = b"".join(b"\x00" + bytes(img[y * W * 3:(y + 1) * W * 3]) for y in range(H))


def chunk(t, d):
    return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)


open(out, "wb").write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", W, H, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b""))
print(f"wrote {out} {W}x{H}")
