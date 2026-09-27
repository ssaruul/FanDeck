using System.Runtime.InteropServices;
using System.Text;
using FanDeck.Hardware;

namespace FanDeck;

/// <summary><c>FanDeck.exe --detect [file]</c>: lists what FanDeck can see, without changing anything.</summary>
static class Detect
{
    public static int Run(string? outFile)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FanDeck {Application.ProductVersion} hardware report, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine($"Driver:   PawnIO {PawnIoDriver.Describe()}");

        try
        {
            using var hw = new FanHardware();
            sb.AppendLine($"Board:    {hw.Board}");
            sb.AppendLine($"Fan chip: {hw.Chip ?? "not found"}");
            foreach (var ch in hw.Channels)
            {
                string rpm = ch.Rpm is float r ? $"{r,5:0} RPM" : "    ? RPM";
                string pct = ch.Percent is float p ? $"{p,3:0}%" : "  ?%";
                string state = !ch.Connected ? "empty"
                    : ch.ControlledByDefault ? "fan connected, FanDeck controls it"
                    : "fan connected, left to the BIOS unless you opt in";
                sb.AppendLine($"  [{ch.Index}] {ch.DisplayName,-18} {rpm}  at {pct}  {state}");
            }
            sb.AppendLine($"CPU:      {hw.CpuName ?? "not found"}  {hw.CpuTempName} = {Temp(hw.CpuTemp)}");
            sb.AppendLine($"GPU:      {hw.GpuName ?? "not found"}  {hw.GpuTempName} = {Temp(hw.GpuTemp)}");
            if (hw.Problem is not null)
                sb.AppendLine($"Problem:  {hw.Problem}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Fan hardware error: {ex.Message}");
        }

        sb.AppendLine();
        using (var aura = AuraUsb.TryOpen(out string? error))
        {
            if (aura is null)
            {
                sb.AppendLine($"Aura RGB: {error}");
            }
            else
            {
                sb.AppendLine($"Aura RGB: firmware {aura.Firmware}, {aura.ArgbHeaders} ARGB headers, " +
                              $"{aura.BoardLeds} board LEDs ({aura.RgbHeaders} of them 12V RGB headers)");
                foreach (var z in aura.Zones)
                    sb.AppendLine($"  zone: {z.Name} (effect channel {z.EffectChannel})");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"{AsusServices.FanService}: {AsusServices.Describe(AsusServices.FanService)}");
        sb.AppendLine($"{AsusServices.LightingService}: {AsusServices.Describe(AsusServices.LightingService)}");

        string text = sb.ToString();
        string path = outFile ?? Path.Combine(Path.GetTempPath(), "FanDeck-detect.txt");
        File.WriteAllText(path, text);

        if (AttachConsole(-1))
            Console.WriteLine(Environment.NewLine + text);
        else if (outFile is null)
            MessageBox.Show(text, "FanDeck: detected hardware");
        return 0;
    }

    static string Temp(float? celsius) => celsius is float c ? $"{c:0.0} °C" : "n/a";

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
