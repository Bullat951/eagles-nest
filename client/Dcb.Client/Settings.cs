using System.IO;
using System.Text.Json;
using Dcb.Client.Core;

namespace Dcb.Client;

public enum BaseMap { Scope, Satellite, DcsCharts, DcsMap, DcsSatellite }

/// <summary>Reads base map names, mapping the ones since removed (2026-10-21) to the nearest
/// remaining map, so older settings files still load.</summary>
public sealed class BaseMapConverter : System.Text.Json.Serialization.JsonConverter<BaseMap>
{
    public override BaseMap Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var name = reader.GetString();
        if (Enum.TryParse<BaseMap>(name, out var map)) return map;
        return name switch
        {
            "DcsTerrain" => BaseMap.DcsMap,
            "DcsImagery" => BaseMap.DcsSatellite,
            _ => BaseMap.Scope,  // OpenStreetMap, or anything unknown
        };
    }

    public override void Write(Utf8JsonWriter writer, BaseMap value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>Where the controller's radio stands for SRS range and line of sight.</summary>
public enum SrsPosition { Off, AtcAirfield, Fixed }

/// <summary>The bridge to the controller's own SRS client (docs/PLAN.md, phase 5a). Ports are
/// SRS's defaults for controller tools.</summary>
public sealed class SrsSettings
{
    public SrsPosition Position { get; set; } = SrsPosition.AtcAirfield;
    public double? FixedLat { get; set; }
    public double? FixedLon { get; set; }
    /// <summary>Also make a tuned radio the one that transmits, when the caller does not say.</summary>
    public bool SelectOnTune { get; set; }
    public int PositionPort { get; set; } = 10710;
    public int LineOfSightPort { get; set; } = 10711;
    public int CommandPort { get; set; } = 9040;

    /// <summary>The controller's channels 1–10 as DCB last set them, MHz (index 0 = channel 1;
    /// 7–10 are the packages' and kept with them).</summary>
    public double?[] Channels { get; set; } = new double?[Core.ChannelPlan.Count];
    /// <summary>The channel DCB last made the transmitting one.</summary>
    public int? Transmit { get; set; }
    /// <summary>Start the SRS client app on connecting to a DCB server when it is not running.</summary>
    public bool StartClient { get; set; } = true;
    /// <summary>SR-ClientRadio.exe; null = from the SRS installer's registry entry.</summary>
    public string? ClientPath { get; set; }

    /// <summary>DCB's own listen-only connection to the SRS server (who is on which frequency,
    /// who talks): runs while DCB is connected, on the DCB server's address at this port.</summary>
    public int ListenPort { get; set; } = Core.SrsListener.DefaultPort;
    /// <summary>The name other SRS users see for DCB's listener.</summary>
    public string ListenName { get; set; } = "DCB (listen)";
    /// <summary>The External AWACS Mode password, DPAPI-encrypted like the DCB password.</summary>
    public string? ProtectedEamPassword { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? EamPassword
    {
        get => Secret.Unprotect(ProtectedEamPassword, Entropy);
        set => ProtectedEamPassword = Secret.Protect(value, Entropy);
    }

    private static readonly byte[] Entropy = "DCB SRS EAM"u8.ToArray();
}

/// <summary>Windows DPAPI for the current user: unreadable to other accounts and other PCs.</summary>
internal static class Secret
{
    public static string? Protect(string? value, byte[] entropy) =>
        value is null ? null : Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(value), entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? data, byte[] entropy)
    {
        if (data is null) return null;
        try
        {
            return System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(
                Convert.FromBase64String(data), entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser));
        }
        catch (Exception)
        {
            return null;  // another user's or PC's data, or damaged: ask again
        }
    }
}

/// <summary>A workspace window's screen placement and what it was opened as.</summary>
public sealed class WindowPlacement
{
    public string Id { get; set; } = "";
    /// <summary>"GCI" or "ATC" for extra windows (title, default layout); "" for the main one.</summary>
    public string Preset { get; set; } = "";
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>A package's name, frequency and radio, kept between sessions (members are not).</summary>
public sealed class PackageSettings
{
    public string? Name { get; set; }
    public double? Mhz { get; set; }
}

/// <summary>Per map window settings, keyed by the window's layout ContentId.</summary>
public sealed class MapSettings
{
    /// <summary>Ground units (vehicles, structures); null = the window's default (hidden in an
    /// ATC window, shown otherwise).</summary>
    public bool? ShowGroundUnits { get; set; }
    /// <summary>Aircraft on the ground or a deck; null = the default (hidden).</summary>
    public bool? ShowLanded { get; set; }
    public BaseMap BaseMap { get; set; } = BaseMap.Scope;
    public LabelLevel Labels { get; set; } = LabelLevel.Full;
    public bool ThreatRings { get; set; } = true;
    /// <summary>The coalition's F10 map marks (players', scripts', DCB's).</summary>
    public bool ShowMarks { get; set; } = true;
    public bool FootholdZones { get; set; } = true;
    public bool ShowAirfields { get; set; } = true;
    /// <summary>Airport-diagram pictures under the live airfield data, when a folder is set.</summary>
    public bool ShowDiagrams { get; set; } = true;
    /// <summary>MGRS grid on the DCS Map base map.</summary>
    public bool ShowGrid { get; set; }
    /// <summary>Base map brightness, 0.2..1; markers and overlays are not dimmed.</summary>
    public double Brightness { get; set; } = 1.0;
    public double? CenterX { get; set; }
    public double? CenterY { get; set; }
    public double? Resolution { get; set; }
}

public sealed class AppSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = Core.DcbConnection.DefaultPort;
    /// <summary>Hold this key for the cursor's bullseye call (a WPF Key name: "B", "Space", "LeftShift"…).</summary>
    public string BullseyeKey { get; set; } = "B";
    /// <summary>Press this key over a map for the RWR clock of the selected (or pointed-at) own
    /// aircraft: the pilot's spike as a bearing line.</summary>
    public string RwrKey { get; set; } = "R";
    /// <summary>"System" (follow Windows' app mode), "Dark" or "Light".</summary>
    public string Theme { get; set; } = "System";
    /// <summary>"Remember" ticked in the connect dialog: the password is kept, encrypted with
    /// Windows DPAPI for the current user (unreadable to other accounts and other PCs).</summary>
    public bool RememberLogin { get; set; }
    public string? ProtectedPassword { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? RememberedPassword
    {
        get
        {
            if (ProtectedPassword is null) return null;
            try
            {
                var data = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPassword), Entropy,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(data);
            }
            catch (Exception)
            {
                return null;  // another user's or PC's data, or damaged: ask again
            }
        }
        set => ProtectedPassword = value is null ? null : Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(value), Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser));
    }

    private static readonly byte[] Entropy = "DCB connect"u8.ToArray();
    /// <summary>The DCS install the DCS maps, charts and airfield data are read from: null = search
    /// for it, "" = the controller chose to run without DCS.</summary>
    public string? DcsPath { get; set; }
    /// <summary>The DCS location was confirmed on first start (or later in Window → Map data).</summary>
    public bool DcsPathConfirmed { get; set; }
    /// <summary>Airport-diagram folder (with diagrams.json) per theatre id; never copied.</summary>
    public Dictionary<string, string> Diagrams { get; set; } = [];
    public Dictionary<string, MapSettings> Maps { get; set; } = [];
    public SrsSettings Srs { get; set; } = new();
    public List<PackageSettings> Packages { get; set; } = [];
    /// <summary>Where the main window was.</summary>
    public WindowPlacement? MainPlacement { get; set; }
    /// <summary>Extra workspace windows open at exit, reopened on start.</summary>
    public List<WindowPlacement> Workspaces { get; set; } = [];

    /// <summary>%AppData%\DCB, or DCB_HOME when set (separate profiles, tests).</summary>
    public static string Folder { get; } =
        Environment.GetEnvironmentVariable("DCB_HOME") is { Length: > 0 } home ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DCB");
    public static string LayoutPath => Path.Combine(Folder, "layout.xml");

    /// <summary>Docking layout of a workspace window: layout.xml for the main one.</summary>
    public static string LayoutPathFor(string workspaceId) =>
        workspaceId == Dcb.Client.MainWindow.MainId ? LayoutPath : Path.Combine(Folder, $"layout-{workspaceId}.xml");
    private static string SettingsPath => Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new BaseMapConverter(), new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new();
        }
        catch (Exception)
        {
            // A broken settings file must not stop the client starting; fall back to defaults.
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>ContentId of the ATC tab's airfield map (not a docking window of its own).</summary>
    public const string AtcMapId = "atc:airfield";

    /// <summary>Airfield the ATC tab showed last, reopened on start.</summary>
    public int? AtcAirfieldId { get; set; }

    public MapSettings MapFor(string contentId)
    {
        if (!Maps.TryGetValue(contentId, out var s))
        {
            // The ATC airfield view starts on the DCS map; general maps on the scope.
            Maps[contentId] = s = new MapSettings { BaseMap = contentId == AtcMapId ? BaseMap.DcsMap : BaseMap.Scope };
        }
        return s;
    }
}
