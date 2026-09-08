using PityLoot.Model;
using PityLoot.Services;
using SPTarkov.Server.Core.Models.Common;
using Xunit;

namespace PityLoot.Tests;

/// <summary>
/// The pity maths, checked against the behaviour of the 3.11 TypeScript this was rewritten
/// from. These are the only part of the mod that can be verified without a running server,
/// so they are deliberately specific about numbers rather than just "it went up".
/// </summary>
public class PityCalculatorTests
{
    private static readonly MongoId Item = new("5449016a4bdc2d6f028b4567");
    private static readonly MongoId Other = new("59faff1d86f7746c51718c9c");
    private static readonly MongoId Roubles = new("5449016a4bdc2d6f028b456f");

    private static PityConfig Config(Action<PityConfig>? tweak = null)
    {
        var config = new PityConfig
        {
            IncreasesStack = true,
            DropRateIncreasePerRaid = 0.2,
            DropRateIncreasePerHour = 0.05,
            MaxDropRateMultiplier = 10,
            KeysAdditionalMultiplier = 2.5,
            DropRateIncreaseType = "raid",
        };
        tweak?.Invoke(config);
        return config;
    }

    private static PityRequirement Req(
        MongoId item, int amount = 1, int raids = 0, long seconds = 0,
        RequirementKind kind = RequirementKind.Hideout, bool fir = false, string? conditionId = null) =>
        new()
        {
            Kind = kind,
            ItemId = item,
            AmountRequired = amount,
            RaidsSinceStarted = raids,
            SecondsSinceStarted = seconds,
            FoundInRaid = fir,
            ConditionId = conditionId,
        };

    // ---- skill levels -------------------------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 0)]      // exactly 10 is still level 0: the original loops on `>`
    [InlineData(11, 1)]
    [InlineData(30, 1)]
    [InlineData(31, 2)]
    public void Skill_level_matches_the_original_curve(double progress, int expected) =>
        Assert.Equal(expected, HideoutRequirementScanner.SkillLevelFromProgress(progress));

    // ---- requirement filtering ---------------------------------------------

    [Fact]
    public void Requirement_is_dropped_when_the_stash_already_covers_it()
    {
        var inventory = new Dictionary<MongoId, ItemCount> { [Item] = new() { NotFoundInRaid = 3 } };

        var incomplete = PityCalculator.FilterIncomplete([Req(Item, amount: 3)], inventory, new Dictionary<string, int>(), true);

        Assert.Empty(incomplete);
    }

    [Fact]
    public void Two_tasks_cannot_both_claim_the_same_stack()
    {
        // One item held, two tasks each wanting one: exactly one stays outstanding.
        var inventory = new Dictionary<MongoId, ItemCount> { [Item] = new() { NotFoundInRaid = 1 } };

        var incomplete = PityCalculator.FilterIncomplete(
            [Req(Item, raids: 1), Req(Item, raids: 5)], inventory, new Dictionary<string, int>(), true);

        Assert.Single(incomplete);
        // The item is spent on the task that has waited least, so the older one keeps pity.
        Assert.Equal(5, incomplete[0].RaidsSinceStarted);
    }

    [Fact]
    public void Found_in_raid_demands_are_served_before_anything_else()
    {
        // Two FiR items, one non-FiR. The FiR quest is satisfied first even though the
        // hideout requirement has waited longer.
        var inventory = new Dictionary<MongoId, ItemCount> { [Item] = new() { FoundInRaid = 1, NotFoundInRaid = 1 } };

        var incomplete = PityCalculator.FilterIncomplete(
            [
                Req(Item, kind: RequirementKind.Hideout, raids: 99),
                Req(Item, kind: RequirementKind.Quest, fir: true, raids: 1, conditionId: "c"),
            ],
            inventory, new Dictionary<string, int>(), true);

        Assert.Empty(incomplete);
    }

    [Fact]
    public void A_non_fir_demand_spends_non_fir_stock_first()
    {
        // Only if the non-FiR pile is short does it dip into FiR, so FiR stock stays
        // available for the demands that actually require it.
        var inventory = new Dictionary<MongoId, ItemCount> { [Item] = new() { FoundInRaid = 1, NotFoundInRaid = 2 } };

        var incomplete = PityCalculator.FilterIncomplete(
            [
                Req(Item, amount: 2, kind: RequirementKind.Hideout),
                Req(Item, amount: 1, kind: RequirementKind.Quest, fir: true, conditionId: "c"),
            ],
            inventory, new Dictionary<string, int>(), true);

        Assert.Empty(incomplete);
    }

    [Fact]
    public void Quest_progress_already_counted_reduces_what_is_still_needed()
    {
        var inventory = new Dictionary<MongoId, ItemCount> { [Item] = new() { NotFoundInRaid = 1 } };
        var progress = new Dictionary<string, int> { ["condition-1"] = 4 };

        var incomplete = PityCalculator.FilterIncomplete(
            [Req(Item, amount: 5, kind: RequirementKind.Quest, conditionId: "condition-1")],
            inventory, progress, true);

        Assert.Empty(incomplete);
    }

    [Fact]
    public void Currency_is_never_boosted()
    {
        var incomplete = PityCalculator.FilterIncomplete(
            [Req(Roubles, amount: 500_000)], new Dictionary<MongoId, ItemCount>(), new Dictionary<string, int>(), true);

        Assert.Empty(incomplete);
    }

    [Fact]
    public void An_item_you_hold_none_of_is_always_outstanding()
    {
        var incomplete = PityCalculator.FilterIncomplete(
            [Req(Item)], new Dictionary<MongoId, ItemCount>(), new Dictionary<string, int>(), true);

        Assert.Single(incomplete);
    }

    // ---- multipliers --------------------------------------------------------

    [Fact]
    public void Raid_pity_is_additive_from_one()
    {
        // 5 raids at +0.2 => 1 + 1.0 = 2.0x
        var multipliers = PityCalculator.BuildItemMultipliers([Req(Item, raids: 5)], Config(), out _);

        Assert.Equal(2.0, multipliers[Item], 6);
    }

    [Fact]
    public void Stacking_adds_two_tasks_together()
    {
        // 5 raids (+1.0) and 4 raids (+0.8) on the same item => 1 + 1.0 + 0.8 = 2.8x
        var multipliers = PityCalculator.BuildItemMultipliers(
            [Req(Item, raids: 5), Req(Item, raids: 4)], Config(), out _);

        Assert.Equal(2.8, multipliers[Item], 6);
    }

    [Fact]
    public void Without_stacking_the_larger_task_wins()
    {
        var multipliers = PityCalculator.BuildItemMultipliers(
            [Req(Item, raids: 5), Req(Item, raids: 4)], Config(c => c.IncreasesStack = false), out _);

        Assert.Equal(2.0, multipliers[Item], 6);
    }

    [Fact]
    public void Stacking_is_order_independent()
    {
        var config = Config();
        var ascending = PityCalculator.BuildItemMultipliers([Req(Item, raids: 1), Req(Item, raids: 7)], config, out _);
        var descending = PityCalculator.BuildItemMultipliers([Req(Item, raids: 7), Req(Item, raids: 1)], config, out _);

        Assert.Equal(ascending[Item], descending[Item], 6);
    }

    [Fact]
    public void Time_mode_counts_whole_hours()
    {
        // 3h36m rounds to 4 hours; 4 * 0.05 => 1.2x
        var multipliers = PityCalculator.BuildItemMultipliers(
            [Req(Item, seconds: 3 * 3600 + 36 * 60)], Config(c => c.DropRateIncreaseType = "time"), out _);

        Assert.Equal(1.2, multipliers[Item], 6);
    }

    [Fact]
    public void Raid_and_time_counters_do_not_leak_into_each_other()
    {
        // Plenty of hours but zero raids: in raid mode the item gets no boost at all.
        var multipliers = PityCalculator.BuildItemMultipliers([Req(Item, seconds: 100 * 3600)], Config(), out _);

        Assert.Equal(1.0, multipliers[Item], 6);
    }

    [Fact]
    public void Keys_are_reported_so_they_can_get_their_extra_multiplier()
    {
        PityCalculator.BuildItemMultipliers(
            [Req(Item, kind: RequirementKind.QuestKey), Req(Other)], Config(), out var keys);

        Assert.Contains(Item, keys);
        Assert.DoesNotContain(Other, keys);
    }

    // ---- applying to a loot row --------------------------------------------

    [Fact]
    public void An_item_with_no_pity_is_left_exactly_as_it_was()
    {
        var result = PityCalculator.Apply(Other, 37, Multipliers(new() { [Item] = 5 }), Config());

        Assert.Equal(37, result);
    }

    [Fact]
    public void Pity_is_clamped_to_the_configured_maximum()
    {
        var result = PityCalculator.Apply(Item, 10, Multipliers(new() { [Item] = 99 }), Config());

        Assert.Equal(100, result); // 10 * min(10, 99)
    }

    [Fact]
    public void Keys_get_the_extra_multiplier_on_top()
    {
        var result = PityCalculator.Apply(Item, 10, Multipliers(new() { [Item] = 2 }, keys: [Item]), Config());

        Assert.Equal(50, result); // 10 * 2 * 2.5
    }

    [Fact]
    public void Wishlist_wins_over_pity_and_is_not_clamped_or_rounded()
    {
        var multipliers = new PityMultipliers(
            new Dictionary<MongoId, double> { [Item] = 99 },
            new Dictionary<MongoId, double> { [Item] = 1.5 },
            new HashSet<MongoId>(),
            new HashSet<MongoId>());

        Assert.Equal(4.5, PityCalculator.Apply(Item, 3, multipliers, Config()), 6);
    }

    [Fact]
    public void Result_is_rounded_like_the_original()
    {
        // 3 * 1.5 = 4.5 -> 5 (the original used JS Math.round, which rounds .5 up)
        var result = PityCalculator.Apply(Item, 3, Multipliers(new() { [Item] = 1.5 }), Config());

        Assert.Equal(5, result);
    }

    private static PityMultipliers Multipliers(Dictionary<MongoId, double> byItem, HashSet<MongoId>? keys = null) =>
        new(byItem, new Dictionary<MongoId, double>(), keys ?? new HashSet<MongoId>(), new HashSet<MongoId>());
}
