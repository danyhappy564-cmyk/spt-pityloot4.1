using PityLoot.Services;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace PityLoot;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.bakahashi.pityloot";
    public string Name { get; init; } = "PityLoot";
    public string Author { get; init; } = "Bakahashi";
    public SemanticVersioning.Version Version { get; init; } = new("3.0.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.5");
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; }

    public List<string>? Contributors { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
}

/// <summary>
/// Boot hook. There is nothing to install here — the loot rewriting rides SPT 4.1's own
/// <c>LazyLoad</c> transformer mechanism and the counters ride static routers, both of
/// which register themselves. This just reports whether the mod is on and why.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 10)]
public class PityLootMod(ModConfig config, ISptLogger<PityLootMod> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken = default)
    {
        if (!config.Values.Enabled)
        {
            logger.Warning("[PityLoot] disabled in config; loot tables untouched");
            return Task.CompletedTask;
        }

        var mode = config.Values.UsesRaidCounter
            ? $"+{config.Values.DropRateIncreasePerRaid:P0} per raid"
            : $"+{config.Values.DropRateIncreasePerHour:P0} per hour";

        logger.Success(
            $"[PityLoot] active — {mode}, capped at {config.Values.MaxDropRateMultiplier}x " +
            $"(quests: {config.Values.AppliesToQuests}, hideout: {config.Values.AppliesToHideout}, " +
            $"keys: {config.Values.IncludeKeys}, gunsmith: {config.Values.IncludeGunsmith})");

        return Task.CompletedTask;
    }
}
