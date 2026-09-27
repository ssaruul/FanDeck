using System.Text.Json;
using System.Text.Json.Serialization;
using FanDeck.Cooling;
using FanDeck.Hardware;

namespace FanDeck;

public sealed class FanSettings
{
    public string? Name { get; set; }
    public int ManualPercent { get; set; } = 50;

    /// <summary>Which temperature this fan follows. null = decide from the header name (the CPU fan follows the CPU).</summary>
    public TempSource? Source { get; set; }

    /// <summary>Older configs stored the choice as "follow the GPU too"; read once and converted to <see cref="Source"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? FollowGpu { get; set; }

    /// <summary>null = decide from the header name (unknown headers and pumps are left to the BIOS).</summary>
    public bool? Controlled { get; set; }

    /// <summary>This fan's edited curves, keyed by profile name. Profiles not listed use the built-in curve.</summary>
    public Dictionary<string, CurvePoint[]> Curves { get; set; } = [];
}

public sealed class LightingSettings
{
    public AuraEffect Effect { get; set; } = AuraEffect.Static;
    public string Color { get; set; } = "#FF0000";

    /// <summary>Index into the Aura zones, or -1 for all of them.</summary>
    public int Target { get; set; } = -1;
}

/// <summary>How an ASUS service was set up before FanDeck disabled it.</summary>
public sealed class ServiceBackup
{
    public int Start { get; set; }
    public bool Delayed { get; set; }
}

/// <summary>Settings stored in %APPDATA%\FanDeck\config.json.</summary>
public sealed class AppConfig
{
    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FanDeck");

    static string FilePath => Path.Combine(Folder, "config.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // The fan loop thread and the UI thread both touch Fans.
    readonly object _gate = new();

    public Profile Profile { get; set; } = Profile.Balanced;

    /// <summary>Game-safe mode turned on by hand: the BIOS runs the fans and the PawnIO driver is unloaded.</summary>
    public bool GameSafe { get; set; }

    /// <summary>
    /// Step aside while FACEIT runs. FACEIT blocked PawnIO before version 2.1; newer versions are reported to work,
    /// but FanDeck stays out of its way unless you turn this off.
    /// </summary>
    public bool AutoGameSafeForFaceit { get; set; } = true;
    public Dictionary<string, FanSettings> Fans { get; set; } = [];
    public LightingSettings Lighting { get; set; } = new();
    public Dictionary<string, ServiceBackup> ServiceBackups { get; set; } = [];

    /// <summary>
    /// Older configs had one curve per profile shared by every fan. They are copied to each fan by
    /// <see cref="MigrateSharedCurves"/> once the fans are known, and then dropped.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, CurvePoint[]>? Curves { get; set; }

    public FanSettings Fan(string id)
    {
        lock (_gate)
        {
            if (!Fans.TryGetValue(id, out var fan))
                Fans[id] = fan = new FanSettings();
            return fan;
        }
    }

    /// <summary>The curve a fan runs on in a profile. Stored arrays are never modified, so this is safe to read from the fan loop.</summary>
    public CurvePoint[] CurveFor(Profile profile, string fanId)
    {
        lock (_gate)
            return Fan(fanId).Curves.TryGetValue(profile.ToString(), out var curve) ? curve : Profiles.DefaultCurve(profile);
    }

    public bool HasCustomCurve(Profile profile, string fanId)
    {
        lock (_gate)
            return Fan(fanId).Curves.ContainsKey(profile.ToString());
    }

    /// <summary>Stores a fan's edited curve, or goes back to the built-in one when <paramref name="points"/> is null.</summary>
    public void SetCurve(Profile profile, string fanId, IEnumerable<CurvePoint>? points)
    {
        var curve = Profiles.Normalize(points);
        lock (_gate)
        {
            var curves = Fan(fanId).Curves;
            if (curve is null)
                curves.Remove(profile.ToString());
            else
                curves[profile.ToString()] = curve;
        }
    }

    /// <summary>Gives each fan a copy of the old shared curves it doesn't already have. Returns true if anything changed.</summary>
    public bool MigrateSharedCurves(IEnumerable<string> fanIds)
    {
        lock (_gate)
        {
            if (Curves is null)
                return false;
            foreach (string id in fanIds)
            {
                var own = Fan(id).Curves;
                foreach (var (profile, curve) in Curves)
                    own.TryAdd(profile, curve);
            }
            Curves = null;
            return true;
        }
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Json) ?? new AppConfig();
                // A hand-edited or damaged curve must not reach the fans.
                if (cfg.Curves is not null)
                    cfg.Curves = CleanCurves(cfg.Curves);
                foreach (var fan in cfg.Fans.Values)
                {
                    fan.Curves = CleanCurves(fan.Curves ?? []);
                    if (fan.Source is null && fan.FollowGpu is bool followGpu)
                        fan.Source = followGpu ? TempSource.Hottest : TempSource.Cpu;
                    fan.FollowGpu = null;
                }
                return cfg;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Could not read config, using defaults: " + ex.Message);
        }
        return new AppConfig();
    }

    static Dictionary<string, CurvePoint[]> CleanCurves(Dictionary<string, CurvePoint[]> curves)
    {
        var clean = new Dictionary<string, CurvePoint[]>();
        foreach (var (name, points) in curves)
        {
            if (Enum.TryParse<Profile>(name, out var p) && Profiles.HasCurve(p) && Profiles.Normalize(points) is { } curve)
                clean[name] = curve;
        }
        return clean;
    }

    public void Save()
    {
        try
        {
            string text;
            lock (_gate)
                text = JsonSerializer.Serialize(this, Json);
            Directory.CreateDirectory(Folder);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write("Could not save config: " + ex.Message);
        }
    }
}
