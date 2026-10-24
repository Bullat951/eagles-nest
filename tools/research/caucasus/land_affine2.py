import json, sys
S = 10000 / 256; SP = 38.45
CP = -320000 / S - 125 + 320000 / SP
CQ = 636000 / S + 288 - 636000 / SP
obs = """Anapa-Vityazevo -6 -20|Krasnodar-Center -13 -11|Novorossiysk -2 -11|Krymsk -12 -24|Maykop-Khanskaya -9 -13|Gelendzhik -8 -19|Sochi-Adler -5 -9|Krasnodar-Pashkovsky -7 -13|Sukhumi-Babushara -14 -22|Gudauta -21 -9|Batumi -14 -6|Senaki-Kolkhi -15 -12|Kobuleti -23 -1|Kutaisi -10 15|Mineralnye Vody -23 11|Nalchik -9 4|Mozdok -11 -11|Tbilisi-Lochini -11 -3|Soganlug -20 10|Vaziani -20 7|Beslan -13 5"""
f = {a["name"]: a["runways"][0] for a in json.load(open(sys.argv[1], encoding="utf-8-sig"))}
rows = []
for item in obs.split("|"):
    *name, dp, dq = item.split(" ")
    n = " ".join(name); r = f[n]
    rows.append((n, r["x"], r["z"], r["x"] / SP + CP + int(dp), r["z"] / SP + CQ + int(dq)))
def lsq(cols, y):
    n = len(cols)
    A = [[sum(cols[i][k] * cols[j][k] for k in range(len(y))) for j in range(n)] for i in range(n)]
    B = [sum(cols[i][k] * y[k] for k in range(len(y))) for i in range(n)]
    for c in range(n):
        p = max(range(c, n), key=lambda r: abs(A[r][c])); A[c], A[p] = A[p], A[c]; B[c], B[p] = B[p], B[c]
        for r in range(n):
            if r != c:
                k = A[r][c] / A[c][c]; A[r] = [a - k * b for a, b in zip(A[r], A[c])]; B[r] -= k * B[c]
    return [B[i] / A[i][i] for i in range(n)]
X = [r[1] for r in rows]; Z = [r[2] for r in rows]; one = [1.0] * len(rows)
a, b, c = lsq([X, Z, one], [r[3] for r in rows])
d, e, g = lsq([X, Z, one], [r[4] for r in rows])
print(f"P = {a:.8f} x + {b:.8f} z + {c:.2f}    -> {1/a:.4f} m/px along x")
print(f"Q = {d:.8f} x + {e:.8f} z + {g:.2f}    -> {1/e:.4f} m/px along z")
import math
print(f"rotation/shear: b {b*1e6:.2f}e-6 d {d*1e6:.2f}e-6 (~{math.degrees(b/a):.3f} / {math.degrees(d/e):.3f} deg)")
rp = [a * x + b * zz + c - p for _, x, zz, p, _ in rows]; rq = [d * x + e * zz + g - q for _, x, zz, _, q in rows]
print(f"residuals px: P rms {math.sqrt(sum(v*v for v in rp)/len(rp)):.1f} max {max(map(abs, rp)):.1f}; Q rms {math.sqrt(sum(v*v for v in rq)/len(rq)):.1f} max {max(map(abs, rq)):.1f}")
# simpler: uniform scale, no rotation
s = lsq([X + Z, [1.0] * len(rows) + [0.0] * len(rows), [0.0] * len(rows) + [1.0] * len(rows)], [r[3] for r in rows] + [r[4] for r in rows])
rr = [s[0] * x + s[1] - p for _, x, _, p, _ in rows] + [s[0] * zz + s[2] - q for _, _, zz, _, q in rows]
print(f"uniform: {1/s[0]:.4f} m/px, P0 {s[1]:.2f}, Q0 {s[2]:.2f}; residual rms {math.sqrt(sum(v*v for v in rr)/len(rr)):.1f} px max {max(map(abs, rr)):.1f}")
