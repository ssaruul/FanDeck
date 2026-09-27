using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace FanDeck;

/// <summary>
/// Starts FanDeck at logon through a scheduled task with highest privileges, so there's no UAC prompt at every boot.
/// Uses task XML because a task made with plain schtasks flags is killed after 72 hours.
/// </summary>
static class Autostart
{
    const string TaskName = "FanDeck";

    public static bool IsEnabled() => Schtasks($"/Query /TN {TaskName}", out _) == 0;

    public static void Enable()
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string exe = SecurityElement.Escape(Environment.ProcessPath!);
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts FanDeck (fan speed and RGB control) at logon.</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT5S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{exe}</Command><Arguments>--tray</Arguments></Exec>
              </Actions>
            </Task>
            """;

        string file = Path.Combine(Path.GetTempPath(), "FanDeck-task.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try
        {
            if (Schtasks($"/Create /TN {TaskName} /XML \"{file}\" /F", out string output) != 0)
                throw new InvalidOperationException(output.Trim());
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static void Disable()
    {
        if (Schtasks($"/Delete /TN {TaskName} /F", out string output) != 0)
            throw new InvalidOperationException(output.Trim());
    }

    static int Schtasks(string args, out string output)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
