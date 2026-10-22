using System.Diagnostics;

namespace Dcb.Client;

/// <summary>Development aid: time spent in each map layer's OnRender (read by the snapshot zoom test).</summary>
public static class RenderStats
{
    private static readonly Dictionary<string, (int Count, double TotalMs, double MaxMs)> Stats = [];

    public readonly struct Scope(string name) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();

        public void Dispose()
        {
            var ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            lock (Stats)
            {
                Stats.TryGetValue(name, out var s);
                Stats[name] = (s.Count + 1, s.TotalMs + ms, Math.Max(s.MaxMs, ms));
            }
        }
    }

    public static Scope Measure(string name) => new(name);

    /// <summary>Records a duration measured elsewhere (part of a larger step).</summary>
    public static void Add(string name, double ms)
    {
        lock (Stats)
        {
            Stats.TryGetValue(name, out var s);
            Stats[name] = (s.Count + 1, s.TotalMs + ms, Math.Max(s.MaxMs, ms));
        }
    }

    private static readonly Dictionary<string, int> Counters = [];

    /// <summary>Counts an event (e.g. a frame drawn with tiles still missing).</summary>
    public static void Count(string name)
    {
        lock (Counters) Counters[name] = Counters.GetValueOrDefault(name) + 1;
    }

    public static void Reset()
    {
        lock (Stats) Stats.Clear();
        lock (Counters) Counters.Clear();
    }

    public static IEnumerable<string> CounterReport()
    {
        lock (Counters) return Counters.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} = {kv.Value}").ToList();
    }

    public static IEnumerable<string> Report()
    {
        lock (Stats)
            return Stats.OrderByDescending(kv => kv.Value.TotalMs)
                .Select(kv => $"{kv.Key,-14} renders={kv.Value.Count,4} avg={kv.Value.TotalMs / kv.Value.Count,7:F2} ms max={kv.Value.MaxMs,7:F2} ms total={kv.Value.TotalMs,8:F0} ms")
                .ToList();
    }
}
