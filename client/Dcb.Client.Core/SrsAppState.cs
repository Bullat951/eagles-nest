using System.Text.Json;

namespace Dcb.Client.Core;

/// <summary>One radio of the controller's SRS app: frequency and guard (Hz), modulation
/// (<see cref="SrsModulation"/>) and the name SRS gives it.</summary>
public readonly record struct SrsAppRadio(double Hz, int Modulation, string Name, double GuardHz)
{
    public double Mhz => Hz / 1e6;
    public bool Off => Modulation is SrsModulation.Disabled || Hz <= 10000;
}

/// <summary>The state the controller's SRS app sends to 127.0.0.1:7082 ("flight panels") every
/// 200 ms while connected (also in External AWACS Mode): its radios, the one selected to
/// transmit, the one transmitting now and how many clients are tuned to each.</summary>
public sealed record SrsAppState(IReadOnlyList<SrsAppRadio> Radios, int Selected, int? SendingOn, IReadOnlyList<int> Tuned, string Name)
{
    public const int Port = 7082;

    /// <summary>Radio 1–10 (0 is the intercom); null when the app has fewer.</summary>
    public SrsAppRadio? Radio(int n) => n >= 0 && n < Radios.Count ? Radios[n] : null;

    public int TunedOn(int n) => n >= 0 && n < Tuned.Count ? Tuned[n] : 0;

    /// <summary>Reads one packet (SRS 2.4 CombinedRadioState JSON); null when it is not one.</summary>
    public static SrsAppState? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("RadioInfo", out var info) || !info.TryGetProperty("radios", out var radios)) return null;
            var list = new List<SrsAppRadio>();
            foreach (var r in radios.EnumerateArray())
                list.Add(new SrsAppRadio(Num(r, "freq"), (int)Num(r, "modulation"), r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", Num(r, "secFreq")));
            var selected = (int)Num(info, "selected");
            int? sending = root.TryGetProperty("RadioSendingState", out var s) && s.TryGetProperty("IsSending", out var on) && on.ValueKind == JsonValueKind.True
                ? (int)Num(s, "SendingOn") : null;
            var tuned = root.TryGetProperty("TunedClients", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.TryGetInt32(out var v) ? v : 0).ToList() : [];
            return new SrsAppState(list, selected, sending, tuned, info.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
