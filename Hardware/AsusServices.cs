using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace FanDeck.Hardware;

/// <summary>
/// The two Armoury Crate services that keep re-applying their own fan and lighting settings.
/// FanDeck disables them while it is in charge, and can put them back exactly as they were.
/// </summary>
public static class AsusServices
{
    public const string FanService = "AsusFanControlService";
    public const string LightingService = "LightingService";
    static readonly string[] Names = [FanService, LightingService];

    public static bool IsRunning(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return sc.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending);
        }
        catch (InvalidOperationException)
        {
            return false; // not installed
        }
    }

    public static string Describe(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return $"{sc.Status} (start: {sc.StartType})";
        }
        catch (InvalidOperationException)
        {
            return "not installed";
        }
    }

    /// <summary>Stops and disables both services, remembering how they were started.</summary>
    public static void TakeOver(AppConfig cfg)
    {
        foreach (string name in Names)
        {
            if (!Exists(name))
                continue;
            if (!cfg.ServiceBackups.ContainsKey(name))
                cfg.ServiceBackups[name] = ReadStart(name);
            cfg.Save();

            Sc($"config {name} start= disabled");
            using var sc = new ServiceController(name);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
        }
    }

    /// <summary>Restores the start type each service had and starts the ones that were automatic.</summary>
    public static void GiveBack(AppConfig cfg)
    {
        foreach (var (name, backup) in cfg.ServiceBackups.ToList())
        {
            if (Exists(name))
            {
                string start = backup.Start switch
                {
                    2 => backup.Delayed ? "delayed-auto" : "auto",
                    3 => "demand",
                    4 => "disabled",
                    _ => "auto",
                };
                Sc($"config {name} start= {start}");
                if (backup.Start == 2)
                {
                    using var sc = new ServiceController(name);
                    if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                    }
                }
            }
            cfg.ServiceBackups.Remove(name);
            cfg.Save();
        }
    }

    static bool Exists(string name) => ServiceController.GetServices().Any(s => s.ServiceName == name);

    static ServiceBackup ReadStart(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        return new ServiceBackup
        {
            Start = key?.GetValue("Start") as int? ?? 2,
            Delayed = (key?.GetValue("DelayedAutostart") as int? ?? 0) == 1,
        };
    }

    static void Sc(string args)
    {
        var psi = new ProcessStartInfo("sc.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"sc {args} failed: {output.Trim()}");
    }
}
