using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RunnerWatch.Core;

[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class I18nJson : JsonSerializerContext;

/// <summary>
/// UI strings. One JSON file per language in <c>src/RunnerWatch.Core/i18n</c>; English is the fallback
/// for any missing key. The language follows the OS (Windows display language, LANG on Linux/macOS)
/// unless <c>"language"</c> is set in the config.
/// </summary>
public static class L
{
    static readonly Dictionary<string, string> En = Load("en") ?? new();
    static Dictionary<string, string> _cur = En;

    public static string Language { get; private set; } = "en";

    /// <summary>Language codes shipped in the binary (e.g. "en", "pt-BR").</summary>
    public static IReadOnlyList<string> Available { get; } = typeof(L).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("i18n.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
        .Select(n => n["i18n.".Length..^".json".Length])
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToList();

    /// <param name="preferred">"auto"/null = follow the OS; otherwise a code like "pt-BR".</param>
    public static void Init(string? preferred = null, CultureInfo? culture = null)
    {
        var code = Resolve(preferred, culture ?? CultureInfo.CurrentUICulture);
        Language = code;
        _cur = code == "en" ? En : Load(code) ?? En;
    }

    internal static string Resolve(string? preferred, CultureInfo culture)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && !preferred.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var hit = Available.FirstOrDefault(a => a.Equals(preferred, StringComparison.OrdinalIgnoreCase))
                      ?? Available.FirstOrDefault(a => Lang(a).Equals(Lang(preferred), StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return Available.FirstOrDefault(a => a.Equals(culture.Name, StringComparison.OrdinalIgnoreCase))
               ?? Available.FirstOrDefault(a => Lang(a).Equals(culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
               ?? "en";
    }

    static string Lang(string code) => code.Split('-')[0];

    public static string T(string key) =>
        _cur.TryGetValue(key, out var v) ? v : En.TryGetValue(key, out var e) ? e : key;

    public static string F(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key), args);

    internal static Dictionary<string, string>? Load(string code)
    {
        using var s = typeof(L).Assembly.GetManifestResourceStream($"i18n.{code}.json");
        return s == null ? null : JsonSerializer.Deserialize(s, I18nJson.Default.DictionaryStringString);
    }
}
