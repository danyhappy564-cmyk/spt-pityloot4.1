using System.Reflection;
using System.Text.Json;
using PityLoot.Model;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;

namespace PityLoot.Services;

/// <summary>
/// Reads the three JSON files next to the dll. They keep the original file names, shapes
/// and camelCase keys so an existing 3.11 config drops straight in.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class ModConfig
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public PityConfig Values { get; private set; } = new();

    /// <summary>quest id → the keys that quest's objectives sit behind.</summary>
    public Dictionary<string, List<string>> QuestKeys { get; private set; } = new();

    /// <summary>quest id → the weapon parts that Gunsmith build needs.</summary>
    public Dictionary<string, List<string>> Gunsmith { get; private set; } = new();

    public ModConfig(ISptLogger<ModConfig> logger)
    {
        var configDir = Path.Combine(
            Path.GetDirectoryName(typeof(ModConfig).Assembly.Location) ?? ".",
            "config");

        Values = Read<PityConfig>(configDir, "config.json", logger) ?? new PityConfig();
        QuestKeys = Read<Dictionary<string, List<string>>>(configDir, "questKeys.json", logger) ?? new();
        Gunsmith = Read<Dictionary<string, List<string>>>(configDir, "gunsmith.json", logger) ?? new();
    }

    private static T? Read<T>(string dir, string file, ISptLogger<ModConfig> logger) where T : class
    {
        var path = Path.Combine(dir, file);
        try
        {
            if (!File.Exists(path))
            {
                logger.Warning($"[PityLoot] {file} not found at {path}; using defaults");
                return null;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (Exception ex)
        {
            // Falling back to defaults rather than throwing: a typo in one config file
            // should not stop the server booting.
            logger.Error($"[PityLoot] could not read {file}, using defaults: {ex.Message}");
            return null;
        }
    }
}
