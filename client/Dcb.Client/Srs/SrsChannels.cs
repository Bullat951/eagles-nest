using Dcb.Client.Core;

namespace Dcb.Client.Srs;

/// <summary>The controller's ten SRS channels (<see cref="ChannelPlan"/>). Read live from the
/// SRS app while it sends its state (<see cref="SrsAppListener"/>); otherwise as DCB last set
/// them (1–6 kept in the settings, 7–10 the packages' frequencies). Setting a channel tunes that
/// radio of the SRS app; transmitting also makes it the radio that transmits.</summary>
public static class SrsChannels
{
    private static SrsSettings Settings => AppState.Settings.Srs;
    private static SrsAppState? Live => SrsAppListener.Instance.State;

    public static event Action? Changed;

    static SrsChannels()
    {
        AppState.Packages.Changed += () => Changed?.Invoke();
        SrsAppListener.Instance.Changed += () =>
        {
            AdoptPackageFrequencies();
            Changed?.Invoke();
        };
    }

    /// <summary>True while the channels shown are the SRS app's own.</summary>
    public static bool IsLive => Live is not null;

    public static double? Mhz(int channel)
    {
        if (Live?.Radio(channel) is { } r) return r.Off ? null : r.Mhz;
        if (ChannelPlan.PackageOf(channel) is > 0 and var p) return AppState.Packages[p].Mhz;
        return channel >= 1 && channel <= Settings.Channels.Length ? Settings.Channels[channel - 1] : null;
    }

    /// <summary>The SRS app's name for a radio ("UHF Guard"…), when read live.</summary>
    public static string? RadioName(int channel) => Live?.Radio(channel)?.Name;

    /// <summary>The channel selected to transmit: the app's, else the one DCB last selected.</summary>
    public static int? Transmitting => Live is { } s ? s.Selected : Settings.Transmit;

    /// <summary>The channel the app is transmitting on this moment, if any.</summary>
    public static int? SendingNow => Live?.SendingOn;

    /// <summary>Clients the app counts on a channel (its own view; for when DCB's SRS
    /// connection is not up).</summary>
    public static int? AppCount(int channel) => Live?.TunedOn(channel);

    /// <summary>Sets a channel's frequency (null clears it in DCB only) and tunes the radio.</summary>
    public static void Set(int channel, double? mhz)
    {
        if (ChannelPlan.PackageOf(channel) is > 0 and var p)
        {
            AppState.Packages[p].Mhz = mhz;
            AppState.Packages.Edited();  // raises Changed through the packages
        }
        else
        {
            if (Settings.Channels.Length < ChannelPlan.Count)
                Settings.Channels = [.. Settings.Channels, .. new double?[ChannelPlan.Count - Settings.Channels.Length]];
            Settings.Channels[channel - 1] = mhz;
            Changed?.Invoke();
        }
        if (mhz is { } f) SrsBridge.Instance.Tune(channel, f, select: false);
    }

    /// <summary>Transmits on a channel at once; a package channel is tuned to the package's
    /// frequency first (JUMP).</summary>
    public static void Transmit(int channel)
    {
        var f = ChannelPlan.PackageOf(channel) is > 0 and var p ? AppState.Packages[p].Mhz ?? Mhz(channel) : Mhz(channel);
        if (f is not { } mhz) return;
        SrsBridge.Instance.Tune(channel, mhz, select: true);
        Settings.Transmit = channel;
        Changed?.Invoke();
    }

    /// <summary>A package with no frequency of its own takes its channel's from the SRS app.</summary>
    private static void AdoptPackageFrequencies()
    {
        if (Live is not { } s) return;
        var adopted = false;
        foreach (var p in AppState.Packages.All)
            if (p.Mhz is null && s.Radio(p.Radio) is { Off: false } r)
            {
                p.Mhz = Math.Round(r.Mhz, 3);
                adopted = true;
            }
        if (adopted) AppState.Packages.Edited();
    }
}
