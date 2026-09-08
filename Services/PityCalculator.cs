using PityLoot.Model;
using SPTarkov.Server.Core.Models.Common;

namespace PityLoot.Services;

/// <summary>How many of an item the player is holding, split by found-in-raid status.</summary>
public sealed class ItemCount
{
    public int FoundInRaid;
    public int NotFoundInRaid;
}

/// <summary>The finished per-item multipliers, ready to be applied to a loot table.</summary>
public sealed record PityMultipliers(
    IReadOnlyDictionary<MongoId, double> ByItem,
    IReadOnlyDictionary<MongoId, double> WishlistByItem,
    IReadOnlySet<MongoId> Keys,
    IReadOnlySet<MongoId> GunsmithParts)
{
    public static readonly PityMultipliers None = new(
        new Dictionary<MongoId, double>(), new Dictionary<MongoId, double>(),
        new HashSet<MongoId>(), new HashSet<MongoId>());

    public bool IsEmpty => ByItem.Count == 0 && WishlistByItem.Count == 0;
}

/// <summary>
/// The pity maths, kept free of SPT services so it can be unit-tested directly against
/// the behaviour of the original TypeScript.
/// </summary>
public static class PityCalculator
{
    /// <summary>Roubles, dollars and euros. Currency is never worth boosting and quests
    /// asking for cash would otherwise flood the loot tables with money.</summary>
    private static readonly HashSet<string> ExcludedItems =
    [
        "569668774bdc2da2298b4568",
        "5449016a4bdc2d6f028b456f",
        "5696686a4bdc2da3298b456a",
    ];

    /// <summary>
    /// Drops every requirement the player can already satisfy out of their stash, and
    /// returns what is genuinely still outstanding.
    ///
    /// <para>Requirements are consumed against a running inventory tally, so two tasks
    /// wanting the same item do not both get to count the same stack. Found-in-raid
    /// requirements are served first (nothing else can use those items), then the rest in
    /// ascending pity order — the task that has been waiting least is satisfied first, so
    /// the one that has waited longest keeps its pity. There is no objectively correct
    /// order here since the player chooses what to hand in; this is the original's
    /// heuristic, kept as-is.</para>
    /// </summary>
    public static List<PityRequirement> FilterIncomplete(
        IEnumerable<PityRequirement> requirements,
        Dictionary<MongoId, ItemCount> inventory,
        IReadOnlyDictionary<string, int> conditionProgress,
        bool useRaidCounter)
    {
        var ordered = requirements.ToList();
        ordered.Sort((a, b) =>
        {
            var aFir = a.Kind == RequirementKind.Quest && a.FoundInRaid;
            var bFir = b.Kind == RequirementKind.Quest && b.FoundInRaid;
            if (aFir != bFir)
            {
                return aFir ? -1 : 1;
            }

            return useRaidCounter
                ? a.RaidsSinceStarted.CompareTo(b.RaidsSinceStarted)
                : a.SecondsSinceStarted.CompareTo(b.SecondsSinceStarted);
        });

        var incomplete = new List<PityRequirement>();

        foreach (var requirement in ordered)
        {
            if (ExcludedItems.Contains(requirement.ItemId.ToString()!))
            {
                continue;
            }

            if (!inventory.TryGetValue(requirement.ItemId, out var held))
            {
                // None at all, so it can never already be satisfied.
                incomplete.Add(requirement);
                continue;
            }

            var satisfied = requirement.Kind switch
            {
                RequirementKind.QuestKey => TakeIfEnough(held, 1, foundInRaid: false),
                RequirementKind.Quest => TakeIfEnough(
                    held,
                    requirement.AmountRequired - (requirement.ConditionId is null
                        ? 0
                        : conditionProgress.GetValueOrDefault(requirement.ConditionId, 0)),
                    requirement.FoundInRaid),
                _ => TakeIfEnough(held, requirement.AmountRequired, foundInRaid: false),
            };

            if (!satisfied)
            {
                incomplete.Add(requirement);
            }
        }

        return incomplete;
    }

    /// <summary>
    /// Consumes <paramref name="amountNeeded"/> from the tally if it is there, and reports
    /// whether it was. A found-in-raid demand can only eat found-in-raid items; anything
    /// else spends the non-FiR pile first so FiR stock stays available for the demands
    /// that actually need it.
    /// </summary>
    private static bool TakeIfEnough(ItemCount held, int amountNeeded, bool foundInRaid)
    {
        if (amountNeeded <= 0)
        {
            return true;
        }

        if (foundInRaid)
        {
            if (held.FoundInRaid < amountNeeded)
            {
                return false;
            }

            held.FoundInRaid -= amountNeeded;
            return true;
        }

        if (held.NotFoundInRaid >= amountNeeded)
        {
            held.NotFoundInRaid -= amountNeeded;
            return true;
        }

        if (held.NotFoundInRaid + held.FoundInRaid < amountNeeded)
        {
            return false;
        }

        amountNeeded -= held.NotFoundInRaid;
        held.NotFoundInRaid = 0;
        held.FoundInRaid -= amountNeeded;
        return true;
    }

    /// <summary>
    /// Turns outstanding requirements into one multiplier per item.
    ///
    /// <para>With <c>increasesStack</c> on, two tasks wanting the same item add their pity
    /// together; with it off the larger of the two wins. Either way the result is the
    /// maximum seen for that item, so ordering does not matter.</para>
    /// </summary>
    public static Dictionary<MongoId, double> BuildItemMultipliers(
        IEnumerable<PityRequirement> incomplete,
        PityConfig config,
        out HashSet<MongoId> keys)
    {
        var raid = new Dictionary<MongoId, double>();
        var time = new Dictionary<MongoId, double>();
        keys = [];

        foreach (var requirement in incomplete)
        {
            if (requirement.Kind == RequirementKind.QuestKey)
            {
                keys.Add(requirement.ItemId);
            }

            var currentRaid = raid.GetValueOrDefault(requirement.ItemId, 1d);
            var currentTime = time.GetValueOrDefault(requirement.ItemId, 1d);

            // Seconds to whole hours, matching the original's rounding.
            var hours = Math.Round(requirement.SecondsSinceStarted / 60d / 60d, MidpointRounding.AwayFromZero);

            var timeCandidate = hours * config.DropRateIncreasePerHour + (config.IncreasesStack ? currentTime : 1d);
            var raidCandidate = requirement.RaidsSinceStarted * config.DropRateIncreasePerRaid + (config.IncreasesStack ? currentRaid : 1d);

            time[requirement.ItemId] = Math.Max(currentTime, timeCandidate);
            raid[requirement.ItemId] = Math.Max(currentRaid, raidCandidate);
        }

        return config.UsesRaidCounter ? raid : time;
    }

    /// <summary>
    /// The final probability for one loot row. Wishlist multipliers take precedence and
    /// are applied raw; pity multipliers are clamped to the configured maximum, get the
    /// extra key multiplier where it applies, and are rounded — all as the original did.
    /// </summary>
    public static double Apply(MongoId tpl, double baseProbability, PityMultipliers multipliers, PityConfig config)
    {
        if (multipliers.WishlistByItem.TryGetValue(tpl, out var wishlist))
        {
            return baseProbability * wishlist;
        }

        if (!multipliers.ByItem.TryGetValue(tpl, out var multiplier))
        {
            return baseProbability;
        }

        var updated = baseProbability * Math.Min(config.MaxDropRateMultiplier, multiplier);

        if (multipliers.Keys.Contains(tpl))
        {
            updated *= config.KeysAdditionalMultiplier;
        }

        return Math.Round(updated, MidpointRounding.AwayFromZero);
    }
}
