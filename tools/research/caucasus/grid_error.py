# Bilinear-grid error for DCS x/z -> lat/lon, modelling each theatre as transverse Mercator
# (community-known DCS parameters: central meridian, scale 0.9996, false easting/northing).
import math

A, F = 6378137.0, 1 / 298.257223563
E2 = F * (2 - F)
K0 = 0.9996

THEATRES = {
    # id: (lon0, false easting, false northing, x0, x1, z0, z1)
    "Caucasus": (33.0, -99516.9998, -4998114.9998, -600000, 380000, -560000, 1130000),
    "Syria": (39.0, 282801.0, -3879865.0, -460000, 380000, -400000, 500000),
}


def forward(lat, lon, lon0):
    """lat/lon -> TM easting, northing (Snyder 8-9..8-10)."""
    phi, lam = math.radians(lat), math.radians(lon - lon0)
    ep2 = E2 / (1 - E2)
    n = A / math.sqrt(1 - E2 * math.sin(phi) ** 2)
    t = math.tan(phi) ** 2
    c = ep2 * math.cos(phi) ** 2
    a = lam * math.cos(phi)
    m = A * ((1 - E2 / 4 - 3 * E2**2 / 64 - 5 * E2**3 / 256) * phi
             - (3 * E2 / 8 + 3 * E2**2 / 32 + 45 * E2**3 / 1024) * math.sin(2 * phi)
             + (15 * E2**2 / 256 + 45 * E2**3 / 1024) * math.sin(4 * phi)
             - (35 * E2**3 / 3072) * math.sin(6 * phi))
    x = K0 * n * (a + (1 - t + c) * a**3 / 6 + (5 - 18 * t + t * t + 72 * c - 58 * ep2) * a**5 / 120)
    y = K0 * (m + n * math.tan(phi) * (a * a / 2 + (5 - t + 9 * c + 4 * c * c) * a**4 / 24
                                         + (61 - 58 * t + t * t + 600 * c - 330 * ep2) * a**6 / 720))
    return x, y


def to_dcs(lat, lon, th):
    lon0, fe, fn = th[:3]
    e, nn = forward(lat, lon, lon0)
    return nn + fn, e + fe  # DCS x = north, z = east


def to_latlon(x, z, th):
    """Inverse by Newton on forward (exact to the model)."""
    lat, lon = 40.0, th[0]
    for _ in range(30):
        x0, z0 = to_dcs(lat, lon, th)
        x1, z1 = to_dcs(lat + 1e-5, lon, th)
        x2, z2 = to_dcs(lat, lon + 1e-5, th)
        a, b, c, d = (x1 - x0) / 1e-5, (x2 - x0) / 1e-5, (z1 - z0) / 1e-5, (z2 - z0) / 1e-5
        det = a * d - b * c
        ex, ez = x - x0, z - z0
        dlat, dlon = (d * ex - b * ez) / det, (-c * ex + a * ez) / det
        lat, lon = lat + dlat, lon + dlon
        if abs(dlat) < 1e-12 and abs(dlon) < 1e-12:
            break
    return lat, lon


def grid_latlon(x, z, th, n):
    x0, x1, z0, z1 = th[3:]
    fi = min(max((x - x0) / (x1 - x0) * (n - 1), 0), n - 1)
    fj = min(max((z - z0) / (z1 - z0) * (n - 1), 0), n - 1)
    i, j = min(int(fi), n - 2), min(int(fj), n - 2)
    ti, tj = fi - i, fj - j
    node = lambda ii, jj: to_latlon(x0 + (x1 - x0) * ii / (n - 1), z0 + (z1 - z0) * jj / (n - 1), th)
    p00, p01, p10, p11 = node(i, j), node(i, j + 1), node(i + 1, j), node(i + 1, j + 1)
    lerp = lambda k: (p00[k] * (1 - tj) + p01[k] * tj) * (1 - ti) + (p10[k] * (1 - tj) + p11[k] * tj) * ti
    return lerp(0), lerp(1)


def metres(lat1, lon1, lat2, lon2):
    dy = (lat2 - lat1) * 111_320
    dx = (lon2 - lon1) * 111_320 * math.cos(math.radians(lat1))
    return math.hypot(dx, dy)


SITES = {
    "Caucasus": [("Kobuleti", 41.9300, 41.8664), ("Kutaisi", 42.1766, 42.4826), ("Batumi", 41.6103, 41.5997),
                 ("Mozdok", 43.7915, 44.6203), ("Anapa", 45.0021, 37.3473)],
    "Syria": [("Akrotiri", 34.5904, 32.9878), ("Incirlik", 37.0021, 35.4259), ("Damascus", 33.4110, 36.5156)],
}

for name, th in THEATRES.items():
    for n in (21, 41, 81):
        errs = []
        for site, lat, lon in SITES[name]:
            x, z = to_dcs(lat, lon, th)
            glat, glon = grid_latlon(x, z, th, n)
            errs.append(f"{site} {metres(lat, lon, glat, glon):.0f} m")
        print(f"{name:9} grid {n:3}: " + ", ".join(errs))
