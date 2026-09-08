using PityLoot.Model;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;

namespace PityLoot.Services;

[Injectable(InjectionType.Singleton)]
public class QuestRequirementScanner(ModConfig config, ISptLogger<QuestRequirementScanner> logger)
{
    /// <summary>The Collector. Excluded on request because it wants one of nearly
    /// everything and would otherwise drown every other task's pity.</summary>
    private const string CollectorQuestId = "5c51aac186f77432ea65c552";

    public List<PityRequirement> GetInProgressRequirements(PmcData pmc, Dictionary<MongoId, Quest> quests, PityTracker tracker)
    {
        var requirements = new List<PityRequirement>();

        foreach (var status in pmc.Quests ?? [])
        {
            if (status.Status != QuestStatusEnum.Started)
            {
                continue;
            }

            if (config.Values.ExcludeCollector && status.QId.ToString() == CollectorQuestId)
            {
                continue;
            }

            if (!quests.TryGetValue(status.QId, out var quest) || quest is null)
            {
                continue;
            }

            requirements.AddRange(GetIncompleteConditions(quest, status, tracker));
        }

        return requirements;
    }

    public List<PityRequirement> GetIncompleteConditions(Quest quest, QuestStatus status, PityTracker tracker)
    {
        var conditions = new List<PityRequirement>();

        tracker.Quests.TryGetValue(status.QId.ToString(), out var pity);
        var raidsSinceStarted = pity?.RaidsSinceStarted ?? 0;

        // StartTime is 0 on some profiles; fall back to the status timer for Started.
        var startTime = status.StartTime > 0
            ? status.StartTime
            : status.StatusTimers?.GetValueOrDefault(QuestStatusEnum.Started) ?? 0;

        // Both are unix seconds. If we cannot work out a start, assume no time has passed
        // rather than inventing pity out of a zero timestamp.
        var secondsSinceStarted = startTime > 0
            ? (long)Math.Round(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - startTime)
            : 0;

        var completed = status.CompletedConditions ?? [];
        var finishConditions = quest.Conditions?.AvailableForFinish ?? [];

        // Every target the quest itself already asks for, so a key listed in questKeys.json
        // that is also a hand-in target does not get counted twice.
        var allTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var condition in finishConditions)
        {
            foreach (var target in condition.Target?.List ?? [])
            {
                allTargets.Add(target);
            }
        }

        foreach (var condition in finishConditions)
        {
            if (condition.ConditionType is not ("HandoverItem" or "LeaveItemAtLocation"))
            {
                continue;
            }

            if (completed.Contains(condition.Id.ToString()))
            {
                continue;
            }

            var amount = (int)(condition.Value ?? 0);
            if (amount <= 0)
            {
                continue;
            }

            foreach (var target in condition.Target?.List ?? [])
            {
                conditions.Add(new PityRequirement
                {
                    Kind = RequirementKind.Quest,
                    ConditionId = condition.Id.ToString(),
                    ItemId = new MongoId(target),
                    FoundInRaid = condition.OnlyFoundInRaid ?? false,
                    AmountRequired = amount,
                    SecondsSinceStarted = secondsSinceStarted,
                    RaidsSinceStarted = raidsSinceStarted,
                });
            }
        }

        var questId = quest.Id.ToString();

        if (config.Values.IncludeKeys && config.QuestKeys.TryGetValue(questId!, out var keys))
        {
            foreach (var key in keys)
            {
                if (allTargets.Contains(key))
                {
                    if (config.Values.Debug)
                    {
                        logger.Info($"[PityLoot] skipping key {key}; quest {quest.QuestName ?? questId} already asks for it");
                    }

                    continue;
                }

                conditions.Add(new PityRequirement
                {
                    Kind = RequirementKind.QuestKey,
                    ItemId = new MongoId(key),
                    AmountRequired = 1,
                    SecondsSinceStarted = secondsSinceStarted,
                    RaidsSinceStarted = raidsSinceStarted,
                });
            }
        }

        if (config.Values.IncludeGunsmith && config.Gunsmith.TryGetValue(questId!, out var parts))
        {
            foreach (var part in parts)
            {
                conditions.Add(new PityRequirement
                {
                    Kind = RequirementKind.Gunsmith,
                    ItemId = new MongoId(part),
                    AmountRequired = 1,
                    SecondsSinceStarted = secondsSinceStarted,
                    RaidsSinceStarted = raidsSinceStarted,
                });
            }
        }

        return conditions;
    }
}
