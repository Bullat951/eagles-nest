# Field runs found when every offset is tried (no skipping) but missed by the skipping scan
# that Taxiways.Polylines does: those are swallowed by an earlier accepted run.
import struct, sys


def run_at(data, o):
    n = struct.unpack_from("<I", data, o)[0]
    if not (2 <= n <= 5000 and o + 4 + 24 * n <= len(data)):
        return None
    pts = []
    for k in range(n):
        x, y, z = struct.unpack_from("<3d", data, o + 4 + 24 * k)
        if not (-2e6 < x < 2e6 and -2e6 < z < 2e6 and -500 < y < 6000 and not (x == 0 and z == 0)):
            return None
        if pts and (abs(x - pts[-1][0]) > 3000 or abs(z - pts[-1][1]) > 3000):
            return None
        pts.append((x, z))
    return pts


field = lambda pts: any(abs(x) > 3000 or abs(z) > 3000 for x, z in pts)

data = open(sys.argv[1], "rb").read()
every = {o: r for o in range(0, len(data) - 52) if (r := run_at(data, o)) and field(r)}

kept, swallowers, o = set(), {}, 0
while o + 52 <= len(data):
    r = run_at(data, o)
    if r:
        end = o + 4 + 24 * len(r)
        if field(r):
            kept.add(o)
        for m in every:
            if o < m < end and m not in kept:
                swallowers[m] = (o, len(r), field(r))
        o = end
        continue
    o += 1

missed = [m for m in every if m not in kept]
print(f"field runs at any offset: {len(every)}, kept by the scan: {len(kept)}, missed: {len(missed)}")
for m in sorted(missed)[:20]:
    pts = every[m]
    s = swallowers.get(m)
    print(f"  @{m}: {len(pts)} pts, {pts[0][0]:.0f},{pts[0][1]:.0f} -> {pts[-1][0]:.0f},{pts[-1][1]:.0f}"
          + (f"; inside run @{s[0]} ({s[1]} pts, {'field' if s[2] else 'LOCAL/noise'})" if s else ""))
