using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Dcb.Client.Srs;

/// <summary>The controller's own SRS client app (SR-ClientRadio.exe). DCB can start it but not
/// log it in: SRS has no command line for that, so Connect (External AWACS Mode) is clicked in
/// its window once.</summary>
public static class SrsApp
{
    private const string ProcessName = "SR-ClientRadio";

    private static SrsSettings Settings => AppState.Settings.Srs;

    public static bool Running => Process.GetProcessesByName(ProcessName).Length > 0;

    /// <summary>The settings' path, else the installer's (HKCU\Software\DCS-SR-Standalone).</summary>
    public static string? ExePath
    {
        get
        {
            if (Settings.ClientPath is { Length: > 0 } set) return File.Exists(set) ? set : null;
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\DCS-SR-Standalone", "SRPathStandalone", null) is not string dir) return null;
            var exe = Path.Combine(dir, "Client", ProcessName + ".exe");
            return File.Exists(exe) ? exe : null;
        }
    }

    /// <summary>Starts the app unless it runs; a line saying what happened.</summary>
    public static string EnsureRunning()
    {
        if (Running) return "SRS app running.";
        if (ExePath is not { } exe) return "SRS app not found (install SRS, or set its path in settings.json: Srs.ClientPath).";
        try
        {
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = true });
            return "SRS app started: click Connect in its window (External AWACS Mode) once.";
        }
        catch (Exception ex)
        {
            return $"SRS app did not start: {ex.Message}";
        }
    }
}
