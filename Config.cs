using System.Text.Json;
using System.Text.Json.Serialization;
using FanDeck.Cooling;
using FanDeck.Hardware;

namespace FanDeck;

public sealed class FanSettings
{
    public string? Name { get; set; }
    public int ManualPercent { get; set; } = 50;

    /// <summary>null = decide from the header name (CPU fans follow the CPU only).</summary>
    public bool? FollowGpu { get; set; }

    /// <summary>null = decide from the header name (unknown headers and pumps are left to the BIOS).</summary>
    public bool? Controlled { get; set; }
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

    /// <summary>Your edited curves, keyed by profile name. Profiles not listed use the built-in curve.</summary>
    public Dictionary<string, CurvePoint[]> Curves { get; set; } = [];

    public FanSettings Fan(string id)
    {
        lock (_gate)
        {
            if (!Fans.TryGetValue(id, out var fan))
                Fans[id] = fan = new FanSettings();
            return fan;
        }
    }

    /// <summary>The curve a profile runs on. The array is never modified after it is stored, so it is safe to read from the fan loop.</summary>
    public CurvePoint[] CurveFor(Profile profile)
    {
        lock (_gate)
            return Curves.TryGetValue(profile.ToString(), out var curve) ? curve : Profiles.DefaultCurve(profile);
    }

    public bool HasCustomCurve(Profile profile)
    {
        lock (_gate)
            return Curves.ContainsKey(profile.ToString());
    }

    /// <summary>Stores an edited curve, or goes back to the built-in one when <paramref name="points"/> is null.</summary>
    public void SetCurve(Profile profile, IEnumerable<CurvePoint>? points)
    {
        var curve = Profiles.Normalize(points);
        lock (_gate)
        {
            if (curve is null)
                Curves.Remove(profile.ToString());
            else
                Curves[profile.ToString()] = curve;
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
                foreach (var (name, points) in cfg.Curves.ToList())
                {
                    var curve = Enum.TryParse<Profile>(name, out var p) && Profiles.HasCurve(p) ? Profiles.Normalize(points) : null;
                    if (curve is null)
                        cfg.Curves.Remove(name);
                    else
                        cfg.Curves[name] = curve;
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
