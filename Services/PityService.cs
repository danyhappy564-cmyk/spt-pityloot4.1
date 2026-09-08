using PityLoot.Model;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace PityLoot.Services;

/// <summary>
/// Ties everything together: reads a profile, works out what it still needs, turns that
/// into multipliers, and hands them to the rewriter.
///
/// <para><b>Known limitation, inherited from the original.</b> The loot tables are global
/// but pity is per profile, so on a shared server the last profile to trigger a recompute
/// owns the odds for everyone. That was true of the 3.11 mod too (it assigned to
/// <c>tables.locations</c> per session); single-player is the supported case.</para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public class PityService(
    ModConfig config,
    ProfileHelper profileHelper,
    PityTrackerStore store,
    QuestRequirementScanner questScanner,
    HideoutRequirementScanner hideoutScanner,
    LootTableRewriter rewriter,
    TemplateTable templates,
    HideoutTable hideout,
    ISptLogger<PityService> logger)
{
    /// <summary>
    /// Recomputes the tracker for this profile and republishes the multipliers.
    /// </summary>
    public async Task OnPityChangedAsync(MongoId sessionId, bool incrementRaidCount, CancellationToken cancellationToken = default)
    {
        var pmc = profileHelper.GetPmcProfile(sessionId);
        if (pmc?.Hideout is null)
        {
            // Scav-only sessions and profiles mid-creation land here.
            logger.Warning("[PityLoot] profile not ready yet, skipping pity update");
            return;
        }

        var previous = await store.LoadAsync(sessionId, cancellationToken);
        var upgrades = hideoutScanner.GetPossibleUpgrades(hideout.Areas, pmc);
        var updated = store.Rebuild(previous, pmc, upgrades, incrementRaidCount);
        await store.SaveAsync(sessionId, updated, cancellationToken);

        Republish(sessionId, pmc, updated);
    }

    /// <summary>Recomputes and republishes without touching the stored counters.</summary>
    public async Task RefreshAsync(MongoId sessionId, CancellationToken cancellationToken = default)
    {
        var pmc = profileHelper.GetPmcProfile(sessionId);
        if (pmc?.Hideout is null)
        {
            return;
        }

        Republish(sessionId, pmc, await store.LoadAsync(sessionId, cancellationToken));
    }

    /// <summary>Applies the current multipliers to the bot tables. Called just before a
    /// raid, matching the original's <c>/client/raid/configuration</c> hook.</summary>
    public void RefreshBots() => rewriter.RewriteBots();

    private void Republish(MongoId sessionId, PmcData pmc, PityTracker tracker)
    {
        var requirements = new List<PityRequirement>();

        if (config.Values.AppliesToQuests)
        {
            requirements.AddRange(questScanner.GetInProgressRequirements(pmc, templates.Quests, tracker));
        }

        if (config.Values.AppliesToHideout)
        {
            requirements.AddRange(hideoutScanner.GetRequirements(hideout.Areas, pmc, tracker));
        }

        var incomplete = PityCalculator.FilterIncomplete(
            requirements,
            TallyInventory(pmc),
            ConditionProgress(pmc),
            config.Values.UsesRaidCounter);

        var byItem = PityCalculator.BuildItemMultipliers(incomplete, config.Values, out var keys);

        var gunsmithParts = incomplete
            .Where(r => r.Kind == RequirementKind.Gunsmith)
            .Select(r => r.ItemId)
            .ToHashSet();

        var multipliers = new PityMultipliers(byItem, BuildWishlist(pmc), keys, gunsmithParts);

        if (config.Values.Debug)
        {
            foreach (var requirement in incomplete)
            {
                logger.Info($"[PityLoot] outstanding {requirement.Kind}: {requirement.ItemId} x{requirement.AmountRequired}");
            }

            foreach (var (tpl, multiplier) in byItem)
            {
                logger.Info($"[PityLoot] {(config.Values.UsesRaidCounter ? "raid" : "time")} multiplier for {tpl}: {multiplier:F2}");
            }
        }

        rewriter.Publish(multipliers);
        logger.Info($"[PityLoot] {incomplete.Count} outstanding requirement(s) across {byItem.Count} item(s) for {sessionId}");
    }

    private static Dictionary<MongoId, ItemCount> TallyInventory(PmcData pmc)
    {
        var inventory = new Dictionary<MongoId, ItemCount>();

        foreach (var item in pmc.Inventory?.Items ?? [])
        {
            if (!inventory.TryGetValue(item.Template, out var count))
            {
                inventory[item.Template] = count = new ItemCount();
            }

            var stack = (int)(item.Upd?.StackObjectsCount ?? 1);
            if (item.Upd?.SpawnedInSession ?? false)
            {
                count.FoundInRaid += stack;
            }
            else
            {
                count.NotFoundInRaid += stack;
            }
        }

        return inventory;
    }

    private static Dictionary<string, int> ConditionProgress(PmcData pmc)
    {
        var progress = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, counter) in pmc.TaskConditionCounters ?? [])
        {
            if (counter.Id.ToString() is { } id)
            {
                progress[id] = (int)(counter.Value ?? 0);
            }
        }

        return progress;
    }

    private Dictionary<MongoId, double> BuildWishlist(PmcData pmc)
    {
        var wishlist = new Dictionary<MongoId, double>();
        if (!config.Values.AppliesToWishlist)
        {
            return wishlist;
        }

        foreach (var (tpl, category) in pmc.WishList ?? [])
        {
            wishlist[tpl] = config.Values.WishlistMultipliers.ForCategory(category);
        }

        return wishlist;
    }
}
