using System.Windows;
using Dcb.Client.Core;
using Dcb.Client.Panels;

namespace Dcb.Client;

/// <summary>Services shared by all panels and windows of the client.</summary>
public static class AppState
{
    public static PictureStore Store { get; } = new();
    public static AppSettings Settings { get; } = AppSettings.Load();
    public static ConnectionManager Connection { get; } = new(Store, Application.Current.Dispatcher);

    /// <summary>The controller's takeoff/landing order per airfield and carrier (this client only).</summary>
    public static RunwayQueues Queues { get; } = new();

    /// <summary>The controller's four packages: names, frequencies and radios kept in the
    /// settings, members for the session (they drop out when the aircraft is gone).</summary>
    public static PackageBook Packages { get; } = new();

    /// <summary>RWR bearing lines, placed threats and the coalition's F10 marks (every map).</summary>
    public static GciBoard Board { get; } = new();

    static AppState()
    {
        foreach (var (p, s) in Packages.All.Zip(Settings.Packages))
        {
            p.Name = s.Name ?? p.Name;
            p.Mhz = s.Mhz;
        }
        Packages.Changed += () => Settings.Packages = Packages.All
            .Select(p => new PackageSettings { Name = p.Name, Mhz = p.Mhz }).ToList();
        Store.Changed += () => Packages.Prune(id => Store.Find(id) is not null);
    }

    /// <summary>The map the controller last interacted with; Objects panel centres it.</summary>
    public static MapPanel? ActiveMap { get; set; }

    /// <summary>Foothold zone highlighted on every map (Zones panel or a click on the map).</summary>
    public static string? SelectedZone { get; private set; }
    public static event Action? ZoneSelectionChanged;

    /// <summary>Airfield highlighted on every map (ATC window).</summary>
    public static int? SelectedAirfieldId { get; private set; }
    public static event Action? AirfieldSelectionChanged;

    public static void SelectAirfield(int? id)
    {
        if (SelectedAirfieldId == id) return;
        SelectedAirfieldId = id;
        AirfieldSelectionChanged?.Invoke();
    }

    /// <summary>The carrier picked in the ATC list (its Carrier and LSO views follow it).</summary>
    public static long? SelectedCarrierId { get; private set; }
    public static event Action? CarrierSelectionChanged;

    public static void SelectCarrier(long? id)
    {
        if (SelectedCarrierId == id) return;
        SelectedCarrierId = id;
        CarrierSelectionChanged?.Invoke();
    }

    /// <summary>The bullseye key (Settings.BullseyeKey) is held down: maps show the cursor's
    /// bullseye call. Set by a key handler on every window of the client.</summary>
    public static bool BullsKeyHeld { get; private set; }
    public static event Action? BullsKeyChanged;

    public static void SetBullsKeyHeld(bool held)
    {
        if (BullsKeyHeld == held) return;
        BullsKeyHeld = held;
        BullsKeyChanged?.Invoke();
    }

    /// <summary>The RWR key was pressed: each map asks whether the pointer is over it and opens its
    /// RWR clock if so. Returns true when one did.</summary>
    public static event Func<bool>? RwrClockRequested;

    public static bool RequestRwrClock()
    {
        if (RwrClockRequested is not { } handlers) return false;
        var opened = false;
        foreach (var h in handlers.GetInvocationList().Cast<Func<bool>>()) opened |= h();
        return opened;
    }

    /// <summary>The NCTR key was pressed: the map under the pointer starts its area selection.</summary>
    public static event Func<bool>? NctrRequested;

    public static bool RequestNctr()
    {
        if (NctrRequested is not { } handlers) return false;
        var started = false;
        foreach (var h in handlers.GetInvocationList().Cast<Func<bool>>()) started |= h();
        return started;
    }

    /// <summary>An Alt+click happened while Alt was down: its release must not open the menu bar.</summary>
    public static bool AltClicked { get; set; }

    /// <summary>The own player whose radios the SRS tab shows ("Listen traffic").</summary>
    public static long? ListenUnit { get; private set; }
    public static event Action? ListenUnitChanged;

    public static void ListenTo(long? unit)
    {
        ListenUnit = unit;
        ListenUnitChanged?.Invoke();
    }

    public static void SelectZone(string? name)
    {
        if (SelectedZone == name) return;
        SelectedZone = name;
        ZoneSelectionChanged?.Invoke();
    }
}
