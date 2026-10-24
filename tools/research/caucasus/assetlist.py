import re, struct, sys
path = sys.argv[1]
f = open(path, "rb"); base = struct.unpack_from("<Q", f.read(16), 8)[0]; f.seek(0); d = f.read(base)
# length-prefixed string list: u32 len + chars, walking backwards/forwards from "town_1"
anchor = d.find(b"\x06\x00\x00\x00town_1")
# walk back to the list start
starts = []
p = anchor
# forward walk from a guessed earlier point: scan for a run of consecutive length-prefixed names
def walk(p):
    names = []
    while p + 4 <= len(d):
        n = struct.unpack_from("<I", d, p)[0]
        if not 1 <= n <= 80:
            break
        s = d[p + 4:p + 4 + n]
        if not re.fullmatch(rb"[ -~]+", s):
            break
        names.append((p, s.decode())); p += 4 + n
    return names
# find the earliest p such that walk(p) reaches anchor
best = None
for back in range(4, 40000):
    q = anchor - back
    if q < 0: break
    w = walk(q)
    if w and any(pp == anchor for pp, _ in w) and len(w) > 20:
        best = (q, w)
print("list starts at", best[0], "with", len(best[1]), "names")
names = [n for _, n in best[1]]
for i in (0, 1, 2, 400, 403, 415, 437, 441, 445, 457, 462, 468, len(names) - 1):
    if i < len(names): print(f"  [{i}] {names[i]}")
print("count before list start u32s:", struct.unpack_from("<4I", d, best[0] - 16))
