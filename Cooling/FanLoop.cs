using FanDeck.Hardware;

namespace FanDeck.Cooling;

/// <param name="Source">Which temperature this fan follows.</param>
/// <param name="SourceTemp">That temperature right now.</param>
public sealed record FanStatus(
    string Id, string Name, float? Rpm, float Percent, bool Controlled, TempSource Source, float Floor, float? SourceTemp);

public sealed record LoopSnapshot(
    float? CpuTemp, float? GpuTemp, Profile Profile, IReadOnlyList<FanStatus> Fans, string? Notice);

/// <summary>
/// Once a second: read temperatures, work out each fan's target speed from the profile,
/// move towards it smoothly, and write it to the fan chip.
/// </summary>
public sealed class FanLoop : IDisposable
{
    public const float CpuFloor = 25;   // % minimum for the CPU cooler, whatever temperature it follows
    public const float CaseFloor = 20;  // % minimum for every other fan
    const float PanicTemp = 95;         // the 7900X's limit: at or above this every fan runs flat out
    const float RampUpPerSecond = 10;
    const float RampDownPerSecond = 3;
    static readonly TimeSpan AverageWindow = TimeSpan.FromSeconds(3);

    readonly FanHardware _hw;
    readonly AppConfig _cfg;
    readonly Thread _thread;
    readonly AutoResetEvent _wake = new(false);
    readonly Queue<(DateTime At, float Value)> _cpuSamples = new();
    readonly Queue<(DateTime At, float Value)> _gpuSamples = new();
    readonly Dictionary<string, float> _output = [];
    volatile bool _stopping;
    volatile bool _checkServicesNow = true;
    bool _fanServiceRunning;
    DateTime _nextServiceCheck;
    DateTime _lastTick = DateTime.UtcNow;
    int _failures;

    public LoopSnapshot? Latest { get; private set; }

    public FanLoop(FanHardware hw, AppConfig cfg)
    {
        _hw = hw;
        _cfg = cfg;
        _thread = new Thread(Run) { IsBackground = true, Name = "FanDeck fan loop" };
        _thread.Start();
    }

    /// <summary>Runs the next tick now instead of waiting (after a profile or slider change).</summary>
    public void Poke() => _wake.Set();

    /// <summary>Re-checks Armoury Crate's fan service on the next tick (after taking over or giving back).</summary>
    public void CheckServicesNow()
    {
        _checkServicesNow = true;
        Poke();
    }

    /// <summary>Looks again for headers with a spinning fan; drops headers whose fan is gone.</summary>
    public void Rescan()
    {
        lock (_hw)
        {
            _hw.Update();
            foreach (var ch in _hw.Channels)
            {
                bool spinning = ch.Rpm > 0;
                if (ch.Connected && !spinning)
                {
                    _hw.Release(ch);
                    _output.Remove(ch.Id);
                }
                ch.Connected = spinning;
            }
        }
        Poke();
    }

    void Run()
    {
        while (!_stopping)
        {
            try
            {
                Tick();
                _failures = 0;
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                if (++_failures >= 5)
                {
                    // Something is badly wrong with the hardware access: let the BIOS drive the fans.
                    lock (_hw)
                        _hw.ReleaseAll();
                    _output.Clear();
                    Latest = (Latest ?? new LoopSnapshot(null, null, _cfg.Profile, [], null)) with
                    {
                        Notice = "Fan control failed, so the BIOS is running the fans. See log.txt.",
                    };
                }
            }
            _wake.WaitOne(1000);
        }
    }

    void Tick()
    {
        var now = DateTime.UtcNow;
        float dt = (float)Math.Clamp((now - _lastTick).TotalSeconds, 0.1, 2);
        _lastTick = now;

        if (_checkServicesNow || now >= _nextServiceCheck)
        {
            _fanServiceRunning = AsusServices.IsRunning(AsusServices.FanService);
            _nextServiceCheck = now.AddSeconds(5);
            _checkServicesNow = false;
        }

        lock (_hw)
        {
            _hw.Update();
            float? cpu = Smooth(_cpuSamples, _hw.CpuTemp, now);
            float? gpu = Smooth(_gpuSamples, _hw.GpuTemp, now);
            var profile = _cfg.Profile;
            var fans = new List<FanStatus>();

            foreach (var ch in _hw.Channels.Where(c => c.Connected))
            {
                var settings = _cfg.Fan(ch.Id);
                bool controlled = settings.Controlled ?? ch.ControlledByDefault;
                var sourceKind = SourceOf(ch, settings);
                float floor = ch.IsCpuFan ? CpuFloor : CaseFloor;
                float? source = TempSources.Read(sourceKind, cpu, gpu);
                float percent;

                if (_fanServiceRunning || !controlled)
                {
                    // Armoury Crate is driving the fans (writing too would make them fight),
                    // or this header is left to the BIOS.
                    if (_output.Remove(ch.Id))
                        _hw.Release(ch);
                    percent = ch.Percent ?? 0;
                }
                else
                {
                    float target = Target(profile, ch.Id, settings, floor, cpu, source);
                    float current = _output.TryGetValue(ch.Id, out float o) ? o : ch.Percent ?? target;
                    bool instant = profile is Profile.Manual or Profile.Max || cpu is null;
                    percent = instant ? target
                        : target > current ? Math.Min(target, current + RampUpPerSecond * dt)
                        : Math.Max(target, current - RampDownPerSecond * dt);
                    _hw.SetPercent(ch, percent);
                    _output[ch.Id] = percent;
                }

                fans.Add(new FanStatus(
                    ch.Id, settings.Name ?? ch.DisplayName, ch.Rpm, percent, controlled, sourceKind, floor, source));
            }

            string? notice = _fanServiceRunning
                ? "Armoury Crate's fan service is running, so FanDeck is only watching."
                : _hw.Problem;
            Latest = new LoopSnapshot(cpu, gpu, profile, fans, notice);
        }
    }

    /// <summary>The CPU cooler follows the CPU unless you choose otherwise; other fans follow the hotter of CPU and GPU.</summary>
    public static TempSource SourceOf(FanChannel channel, FanSettings settings) =>
        settings.Source ?? (channel.IsCpuFan ? TempSource.Cpu : TempSource.Hottest);

    float Target(Profile profile, string fanId, FanSettings settings, float floor, float? cpu, float? source)
    {
        // Can't read the CPU, or it's at its limit: full speed whatever the profile,
        // even for a fan that follows the GPU.
        if (cpu is null || source is null || cpu >= PanicTemp || profile == Profile.Max)
            return 100;
        if (profile == Profile.Manual)
            return Math.Clamp(settings.ManualPercent, floor, 100);
        return Math.Max(floor, Profiles.Evaluate(_cfg.CurveFor(profile, fanId), source.Value));
    }

    /// <summary>Average over the last few seconds, so short Ryzen temperature spikes don't rev the fans.</summary>
    static float? Smooth(Queue<(DateTime At, float Value)> samples, float? reading, DateTime now)
    {
        if (reading is null)
        {
            samples.Clear();
            return null;
        }
        samples.Enqueue((now, reading.Value));
        while (samples.Count > 1 && now - samples.Peek().At > AverageWindow)
            samples.Dequeue();
        return samples.Average(s => s.Value);
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(3));
        lock (_hw)
            _hw.ReleaseAll();
    }
}
