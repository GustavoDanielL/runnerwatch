using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GhActionsMonitor;

public sealed class AppConfig
{
    /// <summary>Repositórios "dono/repo". Vazio = descobre automaticamente os seus repos com push mais recente.</summary>
    public List<string> Repos { get; set; } = new();
    /// <summary>Troque para https://SEU-HOST/api/v3/ se usar GitHub Enterprise Server.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.github.com/";
    public int AutoRepoCount { get; set; } = 10;

    public int ActivePollSeconds { get; set; } = 10;
    public int IdlePollSeconds { get; set; } = 60;

    /// <summary>Regex (case-insensitive) aplicada ao nome do workflow/job/título para marcar como deploy.</summary>
    public string DeployPattern { get; set; } = "deploy|release|publish|prod";
    public int StuckQueueMinutes { get; set; } = 5;
    public bool Notifications { get; set; } = true;
    /// <summary>Mostra só execuções disparadas por você.</summary>
    public bool OnlyMine { get; set; } = false;

    /// <summary>URL do agente de métricas por runner self-hosted. {runner} é trocado pelo nome do runner. "" desliga.</summary>
    public string AgentUrlTemplate { get; set; } = "http://{runner}:9465";
    /// <summary>Sobrescreve a URL por nome de runner: { "meu-runner": "http://192.168.0.10:9465" }.</summary>
    public Dictionary<string, string> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? AgentToken { get; set; }

    public bool StartWithWindows { get; set; } = true;
    public double IdleOpacity { get; set; } = 0.6;
    public double Width { get; set; } = 340;

    // Posição: canto esquerdo + distância do fundo (para a janela crescer para cima).
    public double? Left { get; set; }
    public double? Bottom { get; set; }

    [JsonIgnore] public string FilePath { get; private set; } = DefaultPath;

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GhActionsMonitor");
    public static string DefaultPath => Path.Combine(Dir, "config.json");

    static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppConfig Load()
    {
        var path = DefaultPath;
        AppConfig cfg;
        try
        {
            cfg = File.Exists(path)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Opts) ?? new AppConfig()
                : new AppConfig();
        }
        catch (Exception)
        {
            // Config corrompida: não derruba o app, guarda uma cópia e recomeça.
            try { File.Copy(path, path + ".broken", overwrite: true); } catch { }
            cfg = new AppConfig();
        }
        cfg.FilePath = path;
        cfg.Agents = new Dictionary<string, string>(cfg.Agents ?? new(), StringComparer.OrdinalIgnoreCase);
        cfg.Repos ??= new();
        cfg.ActivePollSeconds = Math.Max(5, cfg.ActivePollSeconds);
        cfg.IdlePollSeconds = Math.Max(cfg.ActivePollSeconds, cfg.IdlePollSeconds);
        cfg.AutoRepoCount = Math.Clamp(cfg.AutoRepoCount, 1, 50);
        if (!File.Exists(path)) cfg.Save();
        return cfg;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* não é crítico */ }
    }
}
