using System.Diagnostics;
using System.Management;
using System.ServiceProcess;

namespace FanDeck.Hardware;

/// <summary>
/// The PawnIO kernel driver that FanDeck reaches the fan chip through. Windows loads it at every boot as a
/// software device (ROOT\PAWNIO\0000), so it is in memory whether or not FanDeck runs. Game-safe mode disables
/// that device, which unloads the driver completely, and enables it again afterwards.
/// </summary>
public static class PawnIoDriver
{
    const string ServiceName = "PawnIO";
    const int DeviceDisabled = 22;           // Win32_PnPEntity.ConfigManagerErrorCode
    const int ErrorSuccessRebootRequired = 3010;

    /// <summary>Versions before 2.1.0 were signed with a certificate FACEIT blocks.</summary>
    public static readonly Version FirstFaceitCompatible = new(2, 1, 0);

    public static Version? Version => LibreHardwareMonitor.PawnIo.PawnIo.Version;

    public static bool IsInstalled => Version is not null;

    public static bool IsFaceitCompatible => Version is not null && Version >= FirstFaceitCompatible;

    public static bool IsLoaded
    {
        get
        {
            try
            {
                using var sc = new ServiceController(ServiceName);
                return sc.Status == ServiceControllerStatus.Running;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public static bool IsDisabled => FindDevice() is { Disabled: true };

    public static string Describe() => !IsInstalled
        ? "not installed"
        : $"{Version}, {(IsLoaded ? "loaded" : IsDisabled ? "disabled (unloaded)" : "not loaded")}, " +
          (IsFaceitCompatible ? "new signature accepted by FACEIT" : "OLD signature that FACEIT blocks, update it");

    /// <summary>Removes the driver from the kernel by disabling its device.</summary>
    public static void Unload() => SetEnabled(false);

    /// <summary>Puts the driver back by enabling its device again.</summary>
    public static void Load() => SetEnabled(true);

    static void SetEnabled(bool enable)
    {
        var device = FindDevice() ?? throw new InvalidOperationException("The PawnIO driver is not installed.");
        if (IsLoaded == enable && device.Disabled != enable)
            return;

        string verb = enable ? "enable" : "disable";
        var psi = new ProcessStartInfo("pnputil.exe", $"/{verb}-device \"{device.Id}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode == ErrorSuccessRebootRequired)
            throw new InvalidOperationException($"Windows needs a restart to {verb} the PawnIO driver (another program may be using it).");
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"pnputil could not {verb} PawnIO: {output.Trim()}");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (IsLoaded != enable && DateTime.UtcNow < deadline)
            Thread.Sleep(200);
        if (IsLoaded != enable)
            throw new InvalidOperationException($"PawnIO did not {(enable ? "start" : "stop")} within 10 seconds.");
    }

    static (string Id, bool Disabled)? FindDevice()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE Service = 'PawnIO'");
        foreach (var item in searcher.Get())
        {
            using var device = (ManagementObject)item;
            return ((string)device["PNPDeviceID"], Convert.ToInt32(device["ConfigManagerErrorCode"]) == DeviceDisabled);
        }
        return null;
    }
}
