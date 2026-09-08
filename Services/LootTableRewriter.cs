using PityLoot.Model;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace PityLoot.Services;

/// <summary>
/// Applies the current pity multipliers to the loot and bot tables.
///
/// <para><b>How this differs from the 3.11 original, and why.</b> The TypeScript version
/// kept a private copy of the untouched location tables at startup and rebuilt a whole new
/// table object from it on every change, because it had nowhere else to get pristine base
/// probabilities from — apply a multiplier twice and the odds compound.</para>
///
/// <para>SPT 4.1 makes that unnecessary. Location loot is exposed as
/// <c>LazyLoad&lt;T&gt;</c>, which deserializes from disk on demand and supports
/// <c>AddTransformer</c>: a function run over the freshly-loaded value every time it is
/// rebuilt. So the transformer registered here always starts from the pristine on-disk
/// numbers, and <c>Clear()</c> is all it takes to make the next read recompute. No private
/// copy of the loot tables, no compounding by construction, and no second multi-hundred-
/// megabyte set of tables held in memory for the life of the server.</para>
///
/// <para>Static ammo and bot tables are not lazy-loaded, so those keep the original's
/// approach — snapshot the base numbers once, and always rewrite from the snapshot.</para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public class LootTableRewriter(
    ModConfig config,
    LocationTable locations,
    BotTable bots,
    ISptLogger<LootTableRewriter> logger)
{
    /// <summary>Containers the original seeds missing Gunsmith parts into.</summary>
    private static readonly MongoId[] GunsmithContainers =
    [
        new("5909d5ef86f77467974efbd8"), // Weapon box
        new("5909d76c86f77471e53d2adf"), // Weapon box
        new("5909d7cf86f77470ee57d75a"), // Weapon box
        new("5909d89086f77472591234a0"), // Weapon box
        new("578f87ad245977356274f2cc"), // Wooden crate
    ];

    /// <summary>Containers the original seeds missing quest keys into.</summary>
    private static readonly MongoId[] QuestKeyContainers =
    [
        new("578f8778245977358849a9b5"), // Jacket
        new("578f87b7245977356274f2cd"), // Drawer
    ];

    /// <summary>The lab keycard is treated as a key even though it is not in questKeys.json.</summary>
    private static readonly MongoId KeycardId = new("5c94bbff86f7747ee735c08f");

    /// <summary>PMC bots and the gifter are left alone: their loadouts are the player's
    /// competition, not a loot source to be tilted.</summary>
    private static readonly HashSet<string> IgnoredBotTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "bear", "usec", "gifter",
    };

    private readonly object _gate = new();

    private PityMultipliers _current = PityMultipliers.None;
    private bool _transformersRegistered;

    // Base probabilities for the eagerly-loaded tables, captured before anything is
    // written so every recompute starts from vanilla rather than from its own last answer.
    private List<(StaticAmmoDetails Row, double Base)>? _ammoBaseline;
    private List<(Dictionary<MongoId, double> Pool, MongoId Key, double Base)>? _botBaseline;

    /// <summary>
    /// Publishes a new set of multipliers and invalidates everything derived from them.
    /// Cheap: the actual work happens lazily when the game next asks for loot.
    /// </summary>
    public void Publish(PityMultipliers multipliers)
    {
        lock (_gate)
        {
            _current = multipliers;
            EnsureTransformers();
            InvalidateLocationLoot();
            RewriteStaticAmmo();
        }
    }

    public void RewriteBots()
    {
        lock (_gate)
        {
            var pools = _botBaseline ??= CaptureBotBaseline();
            foreach (var (pool, key, baseChance) in pools)
            {
                pool[key] = PityCalculator.Apply(key, baseChance, _current, config.Values);
            }
        }
    }

    /// <summary>
    /// Hooks every location's loose and static loot exactly once. The transformer closes
    /// over <c>this</c> rather than over a snapshot, so it always reads whatever multipliers
    /// are current when the table is actually rebuilt.
    /// </summary>
    private void EnsureTransformers()
    {
        if (_transformersRegistered)
        {
            return;
        }

        _transformersRegistered = true;
        var hooked = 0;

        foreach (var (name, location) in locations.GetDictionary())
        {
            if (location is null)
            {
                continue;
            }

            location.LooseLoot?.AddTransformer(loose => TransformLooseLoot(name, loose));
            location.StaticLoot?.AddTransformer(TransformStaticLoot);
            hooked++;
        }

        logger.Info($"[PityLoot] hooked loot tables for {hooked} locations");
    }

    private void InvalidateLocationLoot()
    {
        foreach (var location in locations.GetDictionary().Values)
        {
            location?.LooseLoot?.Clear();
            location?.StaticLoot?.Clear();
        }
    }

    private LooseLoot? TransformLooseLoot(string locationName, LooseLoot? loose)
    {
        if (loose?.Spawnpoints is null || _current.IsEmpty)
        {
            return loose;
        }

        var multipliers = _current;

        foreach (var spawnpoint in loose.Spawnpoints)
        {
            if (spawnpoint.ItemDistribution is null || spawnpoint.Template?.Items is null)
            {
                continue;
            }

            // A spawnpoint's distribution is keyed by the item's _id within the template,
            // so resolve each one back to a template id before it can be matched.
            var idToTpl = new Dictionary<string, MongoId>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in spawnpoint.Template.Items)
            {
                if (item.Id.ToString() is { } id)
                {
                    idToTpl[id] = item.Template;
                }
            }

            foreach (var distribution in spawnpoint.ItemDistribution)
            {
                var key = distribution.ComposedKey?.Key;
                if (key is null || !idToTpl.TryGetValue(key, out var tpl) || distribution.RelativeProbability is null)
                {
                    continue;
                }

                var updated = PityCalculator.Apply(tpl, distribution.RelativeProbability.Value, multipliers, config.Values);
                if (config.Values.Trace && Math.Abs(updated - distribution.RelativeProbability.Value) > double.Epsilon)
                {
                    logger.Info($"[PityLoot] {locationName} spawnpoint {spawnpoint.Template.Id}: {tpl} {distribution.RelativeProbability} -> {updated}");
                }

                distribution.RelativeProbability = updated;
            }
        }

        return loose;
    }

    private Dictionary<MongoId, StaticLootDetails>? TransformStaticLoot(Dictionary<MongoId, StaticLootDetails>? staticLoot)
    {
        if (staticLoot is null || _current.IsEmpty)
        {
            return staticLoot;
        }

        var multipliers = _current;

        // Items that cannot be found any other way get seeded into the containers they
        // would realistically live in, at a high relative weight, rather than merely having
        // their (zero) odds multiplied.
        var missingKeys = multipliers.ByItem.Keys
            .Where(tpl => multipliers.Keys.Contains(tpl) || tpl == KeycardId)
            .ToHashSet();
        var missingParts = multipliers.GunsmithParts;

        foreach (var (containerId, container) in staticLoot)
        {
            if (container.ItemDistribution is null)
            {
                continue;
            }

            var rows = container.ItemDistribution.ToList();

            if (GunsmithContainers.Contains(containerId))
            {
                SeedMissing(rows, missingParts, containerId, "gunsmith part");
            }

            if (QuestKeyContainers.Contains(containerId))
            {
                SeedMissing(rows, missingKeys, containerId, "quest key");
            }

            foreach (var row in rows)
            {
                if (row.RelativeProbability is null)
                {
                    continue;
                }

                row.RelativeProbability = (float)PityCalculator.Apply(
                    row.Tpl, row.RelativeProbability.Value, multipliers, config.Values);
            }

            container.ItemDistribution = rows;
        }

        return staticLoot;
    }

    private void SeedMissing(List<ItemDistribution> rows, IReadOnlySet<MongoId> wanted, MongoId containerId, string what)
    {
        if (wanted.Count == 0)
        {
            return;
        }

        var present = rows.Select(r => r.Tpl).ToHashSet();
        foreach (var tpl in wanted)
        {
            if (present.Contains(tpl))
            {
                continue;
            }

            if (config.Values.Debug)
            {
                logger.Info($"[PityLoot] seeding {what} {tpl} into container {containerId}");
            }

            rows.Add(new ItemDistribution { Tpl = tpl, RelativeProbability = 999 });
        }
    }

    private void RewriteStaticAmmo()
    {
        var baseline = _ammoBaseline ??= CaptureAmmoBaseline();
        foreach (var (row, baseProbability) in baseline)
        {
            if (row.Tpl is { } tpl)
            {
                row.RelativeProbability = (float)PityCalculator.Apply(tpl, baseProbability, _current, config.Values);
            }
        }
    }

    private List<(StaticAmmoDetails, double)> CaptureAmmoBaseline()
    {
        var baseline = new List<(StaticAmmoDetails, double)>();
        foreach (var location in locations.GetDictionary().Values)
        {
            foreach (var rows in location?.StaticAmmo?.Values ?? Enumerable.Empty<IEnumerable<StaticAmmoDetails>>())
            {
                foreach (var row in rows)
                {
                    if (row.RelativeProbability is { } probability)
                    {
                        baseline.Add((row, (double)probability));
                    }
                }
            }
        }

        return baseline;
    }

    private List<(Dictionary<MongoId, double>, MongoId, double)> CaptureBotBaseline()
    {
        var baseline = new List<(Dictionary<MongoId, double>, MongoId, double)>();

        foreach (var (botType, bot) in bots.Types)
        {
            if (bot?.BotInventory is null || IgnoredBotTypes.Contains(botType))
            {
                continue;
            }

            foreach (var pool in bot.BotInventory.Equipment?.Values ?? Enumerable.Empty<Dictionary<MongoId, double>>())
            {
                Capture(pool);
            }

            var items = bot.BotInventory.Items;
            if (items is not null)
            {
                Capture(items.Backpack);
                Capture(items.Pockets);
                Capture(items.SecuredContainer);
                Capture(items.SpecialLoot);
                Capture(items.TacticalVest);
            }
        }

        return baseline;

        void Capture(Dictionary<MongoId, double>? pool)
        {
            if (pool is null)
            {
                return;
            }

            foreach (var (key, value) in pool)
            {
                baseline.Add((pool, key, value));
            }
        }
    }
}
