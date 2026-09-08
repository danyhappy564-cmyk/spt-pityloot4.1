using PityLoot.Model;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Enums.Hideout;

namespace PityLoot.Services;

/// <summary>An upgrade the player currently meets every non-item prerequisite for.</summary>
public sealed record HideoutUpgrade(HideoutAreas Area, int Level, List<(MongoId Id, int Count)> RequiredItems);

[Injectable(InjectionType.Singleton)]
public class HideoutRequirementScanner(ISptLogger<HideoutRequirementScanner> logger)
{
    /// <summary>
    /// Skill level from raw progress. Mirrors the original: the first level costs 10, and
    /// each level costs 10 more than the last until it caps at 100 per level.
    /// </summary>
    public static int SkillLevelFromProgress(double progress)
    {
        var xpToLevel = 10d;
        var level = 0;
        while (progress > xpToLevel)
        {
            level += 1;
            progress -= xpToLevel;
            xpToLevel = Math.Min(100, xpToLevel + 10);
        }

        return level;
    }

    public List<PityRequirement> GetRequirements(List<HideoutArea> areas, PmcData pmc, PityTracker tracker)
    {
        var requirements = new List<PityRequirement>();

        foreach (var upgrade in GetPossibleUpgrades(areas, pmc))
        {
            tracker.Hideout.TryGetValue(((int)upgrade.Area).ToString(), out var pity);
            var secondsSinceStarted = pity is { TimeAvailable: > 0 }
                ? (long)Math.Round((PityTrackerStore.NowMs() - pity.TimeAvailable) / 1000d)
                : 0;

            foreach (var (id, count) in upgrade.RequiredItems)
            {
                requirements.Add(new PityRequirement
                {
                    Kind = RequirementKind.Hideout,
                    ItemId = id,
                    AmountRequired = count,
                    RaidsSinceStarted = pity?.RaidsSinceStarted ?? 0,
                    SecondsSinceStarted = secondsSinceStarted,
                });
            }
        }

        return requirements;
    }

    /// <summary>
    /// The next stage of every area whose Area / Skill / TraderLoyalty prerequisites are
    /// already met, with the items that stage still wants. An area whose non-item
    /// prerequisites are unmet is skipped entirely — pity for something you cannot start
    /// would be pity that never resets.
    /// </summary>
    public List<HideoutUpgrade> GetPossibleUpgrades(List<HideoutArea> areas, PmcData pmc)
    {
        // "constructing" counts as the level above, so an in-progress build does not keep
        // asking for the materials the player already spent.
        var builtLevels = new Dictionary<int, int>();
        foreach (var area in pmc.Hideout?.Areas ?? [])
        {
            builtLevels[(int)area.Type] = (area.Constructing ?? false) ? (area.Level ?? 0) + 1 : area.Level ?? 0;
        }

        var skillLevels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in pmc.Skills?.Common ?? [])
        {
            skillLevels[skill.Id.ToString()] = SkillLevelFromProgress(skill.Progress);
        }

        var upgrades = new List<HideoutUpgrade>();

        foreach (var area in areas)
        {
            if (area.Type is null || area.Stages is null)
            {
                continue;
            }

            var currentLevel = builtLevels.GetValueOrDefault((int)area.Type, 0);
            if (!area.Stages.TryGetValue((currentLevel + 1).ToString(), out var nextStage) || nextStage is null)
            {
                continue;
            }

            var canUpgrade = true;
            var requiredItems = new List<(MongoId, int)>();

            foreach (var requirement in nextStage.Requirements ?? [])
            {
                switch (requirement.Type)
                {
                    case "Area":
                        if (requirement.AreaType is null || requirement.RequiredLevel is null)
                        {
                            Warn(area, currentLevel, "area");
                            break;
                        }

                        if (builtLevels.GetValueOrDefault(requirement.AreaType.Value, 0) < requirement.RequiredLevel)
                        {
                            canUpgrade = false;
                        }

                        break;

                    case "Skill":
                        if (requirement.SkillLevel is null || string.IsNullOrEmpty(requirement.SkillName))
                        {
                            Warn(area, currentLevel, "skill");
                            break;
                        }

                        if (skillLevels.GetValueOrDefault(requirement.SkillName, 0) < requirement.SkillLevel)
                        {
                            canUpgrade = false;
                        }

                        break;

                    case "TraderLoyalty":
                        if (requirement.LoyaltyLevel is null)
                        {
                            Warn(area, currentLevel, "trader");
                            break;
                        }

                        var trader = pmc.TradersInfo?.GetValueOrDefault(requirement.TraderId);
                        if ((trader?.LoyaltyLevel ?? 0) < requirement.LoyaltyLevel)
                        {
                            canUpgrade = false;
                        }

                        break;

                    case "Item":
                        if (requirement.Count is null)
                        {
                            Warn(area, currentLevel, "item");
                            break;
                        }

                        requiredItems.Add((requirement.TemplateId, requirement.Count.Value));
                        break;
                }
            }

            if (canUpgrade)
            {
                upgrades.Add(new HideoutUpgrade(area.Type.Value, currentLevel + 1, requiredItems));
            }
        }

        return upgrades;
    }

    private void Warn(HideoutArea area, int currentLevel, string kind) =>
        logger.Warning($"[PityLoot] corrupt {kind} stage requirement for area {area.Id} stage {currentLevel + 1}, ignoring it");
}
