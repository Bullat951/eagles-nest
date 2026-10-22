namespace Dcb.Client.Core;

/// <summary>A package: own aircraft the controller works together, with a name and a radio
/// channel. <see cref="Radio"/> is the controller's own SRS radio kept for it (7–10, see
/// <see cref="ChannelPlan"/>).</summary>
public sealed class Package(int number)
{
    private readonly List<long> _members = [];

    /// <summary>1–4: also its colour and the tag beside its aircraft ("P1").</summary>
    public int Number { get; } = number;
    public string Name { get; set; } = $"Package {number}";
    /// <summary>The package's frequency in MHz; null until set.</summary>
    public double? Mhz { get; set; }
    public int Radio => ChannelPlan.ForPackage(Number);
    public IReadOnlyList<long> Members => _members;
    public string Tag => $"P{Number}";

    internal bool Add(long unit)
    {
        if (_members.Contains(unit)) return false;
        _members.Add(unit);
        return true;
    }

    internal bool Remove(long unit) => _members.Remove(unit);
    internal int RemoveAll(Predicate<long> gone) => _members.RemoveAll(gone);
}

/// <summary>The controller's four packages. An aircraft is in one package at most. Not
/// thread-safe: use from the UI thread.</summary>
public sealed class PackageBook
{
    public const int Count = 4;

    public PackageBook() => All = Enumerable.Range(1, Count).Select(n => new Package(n)).ToArray();

    public IReadOnlyList<Package> All { get; }

    public event Action? Changed;

    public Package this[int number] => All[number - 1];

    public Package? PackageOf(long unit) => All.FirstOrDefault(p => p.Members.Contains(unit));

    /// <summary>Puts a unit into package <paramref name="number"/>, out of any other.</summary>
    public void Add(int number, long unit)
    {
        foreach (var p in All.Where(p => p.Number != number)) p.Remove(unit);
        this[number].Add(unit);
        Changed?.Invoke();
    }

    public void Remove(long unit)
    {
        if (All.Count(p => p.Remove(unit)) > 0) Changed?.Invoke();
    }

    /// <summary>Drops members that no longer exist (shot down, landed and despawned, a new mission).</summary>
    public void Prune(Func<long, bool> exists)
    {
        if (All.Sum(p => p.RemoveAll(u => !exists(u))) > 0) Changed?.Invoke();
    }

    /// <summary>Name, frequency or radio edited: maps and lists redraw.</summary>
    public void Edited() => Changed?.Invoke();
}
