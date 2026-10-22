namespace Dcb.Client.Map.Airfields;

/// <summary>Which recovery cases the Carrier view draws: the case DCS is running now (weather
/// and night, <see cref="Core.Marshal.CaseFor"/>) unless the controller switched a case on or
/// off; those choices are kept in the settings until "auto" clears them.</summary>
public static class CarrierCases
{
    private static Dictionary<string, bool> Overrides => AppState.Settings.CarrierCases;

    /// <summary>The case of the carrier on show (set by the Carrier view as it draws).</summary>
    public static int Current { get; private set; } = 1;

    public static event Action? Changed;

    public static void SetCurrent(int recoveryCase)
    {
        if (Current == recoveryCase) return;
        Current = recoveryCase;
        Changed?.Invoke();
    }

    public static bool Shown(int recoveryCase) =>
        Overrides.TryGetValue(recoveryCase.ToString(), out var on) ? on : recoveryCase == Current;

    public static void Toggle(int recoveryCase)
    {
        Overrides[recoveryCase.ToString()] = !Shown(recoveryCase);
        Changed?.Invoke();
    }

    /// <summary>Back to showing only the current case.</summary>
    public static void Auto()
    {
        Overrides.Clear();
        Changed?.Invoke();
    }

    public static bool IsAuto => Overrides.Count == 0;
}
