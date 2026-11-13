using System.Text.Json;
using System.Text.Json.Serialization;

namespace RunnerWatch.Core;

public sealed class AppConfig
{
    /// <summary>"owner/repo" list. Empty = auto-discover your most recently pushed repos (own, org and collaborator).</summary>
    public List<string> Repos { get; set; } = new();
    /// <summary>GitHub Enterprise Server: https://YOUR-HOST/api/v3/</summary>
    public string ApiBaseUrl { get; set; } = "https://api.github.com/";
    public int AutoRepoCount { get; set; } = 10;

    public int ActivePollSeconds { get; set; } = 10;
    public int IdlePollSeconds { get; set; } = 60;

    /// <summary>Case-insensitive regex applied to workflow/job/title names to flag a deploy.</summary>
    public string DeployPattern { get; set; } = "deploy|release|publish|prod";
    public int StuckQueueMinutes { get; set; } = 5;
    public bool Notifications { get; set; } = true;
    /// <summary>Only show runs triggered by you.</summary>
    public bool OnlyMine { get; set; }
    /// <summary>How long a failed run stays in the widget (with a re-run button). 0 hides failures.</summary>
    public int ShowFailuresMinutes { get; set; } = 15;

    /// <summary>Metrics agent URL per self-hosted runner. {runner} is replaced by the runner name. "" disables.</summary>
    public string AgentUrlTemplate { get; set; } = "http://{runner}:9465";
    /// <summary>Per-runner override: { "my-runner": "http://192.168.0.10:9465" }.</summary>
    public Dictionary<string, string> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? AgentToken { get; set; }

    /// <summary>"auto" follows the OS, or a code such as "en" / "pt-BR".</summary>
    public string Language { get; set; } = "auto";
    /// <summary>"system", "dark" or "light".</summary>
    public string Theme { get; set; } = "system";
    public bool StartWithSystem { get; set; } = true;
    public double IdleOpacity { get; set; } = 0.6;
    public double Width { get; set; } = 340;

    // Position: left edge + bottom edge in pixels, so the window grows upwards.
    public int? Left { get; set; }
    public int? Bottom { get; set; }

    [JsonIgnore] public string FilePath { get; private set; } = DefaultPath;

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "RunnerWatch");
    public static string DefaultPath => Path.Combine(Dir, "config.json");

    /// <summary>Config of the previous name (GhActionsMonitor, Windows only), migrated on first run.</summary>
    static string LegacyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GhActionsMonitor", "config.json");

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        AppConfig cfg;
        try
        {
            bool migrated = false;
            if (!File.Exists(path) && path == DefaultPath && File.Exists(LegacyPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(LegacyPath, path);
                migrated = true;
                Log.Info($"Migrated config from {LegacyPath}");
            }
            cfg = File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJson.Default.AppConfig) ?? new AppConfig()
                : new AppConfig();
            if (migrated)
            {
                // The old app stored the position in WPF device-independent units; now it's pixels.
                cfg.Left = cfg.Bottom = null;
                cfg.FilePath = path;
                cfg.Save();
            }
        }
        catch (Exception ex)
        {
            // Broken config must not crash the app: keep a copy and start from defaults.
            Log.Warn($"Invalid config at {path}; saved as .broken and using defaults", ex);
            try { File.Copy(path, path + ".broken", overwrite: true); } catch { }
            cfg = new AppConfig();
        }
        cfg.FilePath = path;
        cfg.Agents = new Dictionary<string, string>(cfg.Agents ?? new(), StringComparer.OrdinalIgnoreCase);
        cfg.Repos ??= new();
        cfg.ActivePollSeconds = Math.Max(5, cfg.ActivePollSeconds);
        cfg.IdlePollSeconds = Math.Max(cfg.ActivePollSeconds, cfg.IdlePollSeconds);
        cfg.AutoRepoCount = Math.Clamp(cfg.AutoRepoCount, 1, 50);
        cfg.StuckQueueMinutes = Math.Max(1, cfg.StuckQueueMinutes);
        cfg.ShowFailuresMinutes = Math.Clamp(cfg.ShowFailuresMinutes, 0, 24 * 60);
        if (!File.Exists(path)) cfg.Save();
        return cfg;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, ConfigJson.Default.AppConfig));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Log.Warn("Could not save config", ex); }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class ConfigJson : JsonSerializerContext;
