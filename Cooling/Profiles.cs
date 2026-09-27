namespace FanDeck.Cooling;

public enum Profile { Silent, Balanced, Performance, Max, Manual }

/// <summary>One point on a fan curve: at <see cref="Temp"/> °C the fans run at <see cref="Percent"/> %.</summary>
public readonly record struct CurvePoint(float Temp, float Percent);

/// <summary>Which temperature a fan's curve reads.</summary>
public enum TempSource { Cpu, Gpu, Hottest }

public static class TempSources
{
    public static readonly TempSource[] All = [TempSource.Cpu, TempSource.Gpu, TempSource.Hottest];

    public static string Label(TempSource source) => source switch
    {
        TempSource.Cpu => "CPU temperature",
        TempSource.Gpu => "GPU temperature",
        _ => "Hotter of CPU and GPU",
    };

    /// <summary>
    /// The temperature a fan should follow. Falls back to the CPU when the GPU can't be read;
    /// null only when the CPU can't be read either (the fan loop then runs the fan flat out).
    /// </summary>
    public static float? Read(TempSource source, float? cpu, float? gpu) => source switch
    {
        TempSource.Gpu => gpu ?? cpu,
        TempSource.Hottest => cpu is null ? null : gpu is null ? cpu : Math.Max(cpu.Value, gpu.Value),
        _ => cpu,
    };
}

/// <summary>Temperature (°C) to fan-speed (%) curves.</summary>
public static class Profiles
{
    public const float MinTemp = 20;
    public const float MaxTemp = 100;
    public const int MaxPoints = 8;

    public static readonly Profile[] All = [Profile.Silent, Profile.Balanced, Profile.Performance, Profile.Max, Profile.Manual];

    static readonly Dictionary<Profile, CurvePoint[]> Defaults = new()
    {
        [Profile.Silent] = [new(50, 25), new(70, 40), new(85, 70), new(95, 100)],
        [Profile.Balanced] = [new(45, 30), new(65, 50), new(80, 80), new(90, 100)],
        [Profile.Performance] = [new(40, 50), new(60, 70), new(75, 100)],
    };

    /// <summary>Silent, Balanced and Performance have a curve you can edit; Max and Manual don't.</summary>
    public static bool HasCurve(Profile profile) => Defaults.ContainsKey(profile);

    public static CurvePoint[] DefaultCurve(Profile profile) => (CurvePoint[])Defaults[profile].Clone();

    /// <summary>Fan % for a temperature: flat below the first point, flat above the last, linear in between.</summary>
    public static float Evaluate(IReadOnlyList<CurvePoint> points, float temp)
    {
        if (temp <= points[0].Temp)
            return points[0].Percent;

        for (int i = 1; i < points.Count; i++)
        {
            if (temp > points[i].Temp)
                continue;
            var (t0, p0) = points[i - 1];
            var (t1, p1) = points[i];
            return p0 + (p1 - p0) * (temp - t0) / (t1 - t0);
        }
        return points[^1].Percent;
    }

    /// <summary>
    /// Makes a curve safe to use: in range, sorted, one point per degree, and never slower at a higher temperature.
    /// Returns null when fewer than two points survive.
    /// </summary>
    public static CurvePoint[]? Normalize(IEnumerable<CurvePoint>? points)
    {
        if (points is null)
            return null;
        var result = new List<CurvePoint>();
        float highest = 0;
        foreach (var p in points
                     .Select(p => new CurvePoint(
                         MathF.Round(Math.Clamp(p.Temp, MinTemp, MaxTemp)),
                         MathF.Round(Math.Clamp(p.Percent, 0, 100))))
                     .OrderBy(p => p.Temp))
        {
            if (result.Count > 0 && result[^1].Temp == p.Temp)
                result.RemoveAt(result.Count - 1);
            highest = Math.Max(highest, p.Percent);
            result.Add(p with { Percent = highest });
        }
        return result.Count >= 2 ? result.Take(MaxPoints).ToArray() : null;
    }

    public static string Describe(Profile profile) => profile switch
    {
        Profile.Silent => "Quiet. Fans stay slow until the PC gets warm.",
        Profile.Balanced => "Everyday use. Speeds up steadily with temperature.",
        Profile.Performance => "Cooler and louder. Fans ramp up early.",
        Profile.Max => "Every fan at full speed.",
        _ => "You set each fan's speed with its slider.",
    };
}
