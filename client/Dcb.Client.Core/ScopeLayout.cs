namespace Dcb.Client.Core;

/// <summary>Declutters labels on a round scope (the RWR scope): each label wants its centre at a
/// point (bearing and strength); in the order given (most important first) each takes the nearest
/// free spot — stepped along its radial and across it — that stays inside the ring and does not
/// overlap one already placed. With no free spot it keeps its own point.</summary>
public static class ScopeLayout
{
    public static (double X, double Y)[] Declutter(IReadOnlyList<(double X, double Y, double W, double H)> labels,
        double cx, double cy, double radius, double gap = 2)
    {
        var placed = new List<(double X, double Y, double W, double H)>();
        var result = new (double X, double Y)[labels.Count];
        for (int i = 0; i < labels.Count; i++)
        {
            var (x, y, w, h) = labels[i];
            double rx = x - cx, ry = y - cy, len = Math.Sqrt(rx * rx + ry * ry);
            // Unit radial (outward) and tangential directions; straight up at the centre.
            double ux = len > 1e-6 ? rx / len : 0, uy = len > 1e-6 ? ry / len : -1;
            double tx = -uy, ty = ux;
            var candidates = new List<(double X, double Y, double D)>();
            for (int m = -6; m <= 6; m++)
                for (int n = -4; n <= 4; n++)
                {
                    double ox = ux * m * (h + gap) + tx * n * (w + gap) * 0.6, oy = uy * m * (h + gap) + ty * n * (w + gap) * 0.6;
                    candidates.Add((x + ox, y + oy, ox * ox + oy * oy));
                }
            var spot = (x, y);
            foreach (var c in candidates.OrderBy(c => c.D))
            {
                var dc = Math.Sqrt((c.X - cx) * (c.X - cx) + (c.Y - cy) * (c.Y - cy));
                if (c.D > 0 && dc > radius - Math.Max(w, h) / 2) continue;
                if (placed.Any(p => Math.Abs(p.X - c.X) < (p.W + w) / 2 + gap && Math.Abs(p.Y - c.Y) < (p.H + h) / 2 + gap)) continue;
                spot = (c.X, c.Y);
                break;
            }
            placed.Add((spot.Item1, spot.Item2, w, h));
            result[i] = spot;
        }
        return result;
    }
}
