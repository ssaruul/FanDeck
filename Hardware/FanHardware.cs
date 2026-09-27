using LibreHardwareMonitor.Hardware;

namespace FanDeck.Hardware;

/// <summary>One fan header on the Super I/O chip: its speed sensor and its PWM control.</summary>
public sealed class FanChannel
{
    internal FanChannel(ISensor control, ISensor? rpm, string name)
    {
        ControlSensor = control;
        RpmSensor = rpm;
        DisplayName = name;
    }

    internal ISensor ControlSensor { get; }
    internal ISensor? RpmSensor { get; }

    public string Id => ControlSensor.Identifier.ToString();
    public int Index => ControlSensor.Index;
    public string DisplayName { get; }
    public bool IsCpuFan => DisplayName.Contains("CPU", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Only headers we can name, and that aren't pumps, are driven by default. A pump should run flat out,
    /// and an unnamed channel (e.g. "Fan #7" reading 3100 RPM on this board) may not be a case fan at all.
    /// </summary>
    public bool ControlledByDefault =>
        !DisplayName.StartsWith("Fan #", StringComparison.Ordinal) &&
        !DisplayName.Contains("Pump", StringComparison.OrdinalIgnoreCase);
    public float? Rpm => RpmSensor?.Value;
    public float? Percent => ControlSensor.Value;

    /// <summary>A fan was spinning on this header at the last scan. Only these are controlled.</summary>
    public bool Connected { get; internal set; }
}

/// <summary>
/// Fan headers, CPU and GPU temperatures, read and driven through LibreHardwareMonitor (which uses the PawnIO driver).
/// Not thread-safe: callers lock on the instance.
/// </summary>
public sealed class FanHardware : IDisposable
{
    // How ASUS wires the headers on its NCT6799D boards (LibreHardwareMonitor's PRIME B650-PLUS table).
    // LHM has no table for the ROG STRIX B650E-F and would call these "Fan #1".."Fan #7".
    static readonly string[] AsusNct6799Names =
        ["Chassis Fan 1", "CPU Fan", "Chassis Fan 2", "Chassis Fan 3", "CPU Optional Fan", "AIO Pump"];

    readonly Computer _computer;
    readonly IHardware? _superIo;
    readonly IHardware? _cpu;
    readonly IHardware? _gpu;
    readonly ISensor? _cpuTemp;
    readonly ISensor? _gpuTemp;

    public string Board { get; }
    public string? Chip => _superIo?.Name;
    public string? CpuName => _cpu?.Name;
    public string? GpuName => _gpu?.Name;
    public string? CpuTempName => _cpuTemp?.Name;
    public string? GpuTempName => _gpuTemp?.Name;
    public IReadOnlyList<FanChannel> Channels { get; }

    /// <summary>Why fan control is unavailable, or null when it works.</summary>
    public string? Problem { get; }

    public FanHardware()
    {
        _computer = new Computer { IsMotherboardEnabled = true, IsCpuEnabled = true, IsGpuEnabled = true };
        _computer.Open();

        var board = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
        _superIo = board?.SubHardware.FirstOrDefault(h => h.HardwareType == HardwareType.SuperIO);
        _cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        _gpu = PickGpu(_computer.Hardware);

        string maker = _computer.SMBios?.Board?.ManufacturerName ?? "";
        Board = board?.Name ?? "unknown board";

        Update();
        _cpuTemp = PickTemperature(_cpu, "Core (Tctl/Tdie)", "Tctl", "Package", "Core");
        _gpuTemp = PickTemperature(_gpu, "GPU Core", "Core", "Edge");

        bool asusNames = maker.Contains("ASUS", StringComparison.OrdinalIgnoreCase)
                         && (Chip?.Contains("6799", StringComparison.Ordinal) ?? false);
        var fans = _superIo?.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList() ?? [];
        Channels = (_superIo?.Sensors ?? [])
            .Where(s => s.SensorType == SensorType.Control && s.Control is not null)
            .OrderBy(s => s.Index)
            .Select(c => new FanChannel(c, fans.FirstOrDefault(f => f.Index == c.Index), NameFor(c, asusNames)))
            .ToList();

        // Fan speed readings settle after a moment; read twice before deciding which headers have fans.
        Thread.Sleep(300);
        Update();
        foreach (var ch in Channels)
            ch.Connected = ch.Rpm > 0;

        if (_superIo is null)
            Problem = !PawnIoDriver.IsInstalled
                ? "The PawnIO driver isn't installed, so FanDeck can't reach the fan chip. The BIOS is running your fans."
                : !PawnIoDriver.IsLoaded
                    ? "The PawnIO driver isn't loaded (disabled, or blocked by an anti-cheat). The BIOS is running your fans."
                    : "Fan chip not found. FanDeck must run as administrator.";
        else if (Channels.Count == 0)
            Problem = $"{Chip} was found, but it offers no fan controls.";
    }

    public void Update()
    {
        _superIo?.Update();
        _cpu?.Update();
        _gpu?.Update();
    }

    public float? CpuTemp => Valid(_cpuTemp?.Value);
    public float? GpuTemp => Valid(_gpuTemp?.Value);

    public void SetPercent(FanChannel channel, float percent) =>
        channel.ControlSensor.Control?.SetSoftware(Math.Clamp(percent, 0, 100));

    /// <summary>Hands the header back to the BIOS fan curve.</summary>
    public void Release(FanChannel channel)
    {
        var control = channel.ControlSensor.Control;
        if (control?.ControlMode == ControlMode.Software)
            control.SetDefault();
    }

    public void ReleaseAll()
    {
        foreach (var ch in Channels)
        {
            try { Release(ch); }
            catch (Exception ex) { Log.Write($"Could not release {ch.DisplayName}: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        ReleaseAll();
        _computer.Close();
    }

    static string NameFor(ISensor control, bool asusNames) =>
        asusNames && control.Index < AsusNct6799Names.Length ? AsusNct6799Names[control.Index] : control.Name;

    static IHardware? PickGpu(IEnumerable<IHardware> hardware)
    {
        var gpus = hardware
            .Where(h => h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel)
            .ToList();
        // Prefer a discrete card over the Ryzen iGPU.
        return gpus.FirstOrDefault(g => g.HardwareType != HardwareType.GpuIntel &&
                                        (g.Name.Contains(" RX ") || g.Name.Contains("RTX") || g.Name.Contains("GTX")))
               ?? gpus.FirstOrDefault();
    }

    static ISensor? PickTemperature(IHardware? hardware, params string[] preferred)
    {
        if (hardware is null)
            return null;
        var temps = hardware.Sensors.Where(s => s.SensorType == SensorType.Temperature).ToList();
        foreach (string name in preferred)
        {
            var match = temps.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? temps.FirstOrDefault(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }
        return temps.FirstOrDefault();
    }

    static float? Valid(float? celsius) => celsius is > 0 and < 150 ? celsius : null;
}
