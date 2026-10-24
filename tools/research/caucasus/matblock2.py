import re, struct, sys
path = sys.argv[1]
f = open(path, "rb"); base = struct.unpack_from("<Q", f.read(16), 8)[0]; f.seek(0); d = f.read(base)
s, e = 13300400, 13333000
for m in re.finditer(rb"[ -~]{3,}", d[s:e]):
    print(f"{s + m.start():9} {m.group().decode()}")
