using SPTarkov.Server.Core.Models.Common;

namespace PityLoot.Model;

public enum RequirementKind
{
    /// <summary>A HandoverItem / LeaveItemAtLocation condition on a started quest.</summary>
    Quest,

    /// <summary>A key listed in questKeys.json for a started quest.</summary>
    QuestKey,

    /// <summary>A weapon part listed in gunsmith.json for a started Gunsmith quest.</summary>
    Gunsmith,

    /// <summary>An item required by the next stage of a hideout area you can start.</summary>
    Hideout,
}

/// <summary>
/// One outstanding "you still need N of this item" fact, along with how long you have
/// been needing it. Both counters are carried because the pity curve can be driven off
/// either raids or wall-clock hours depending on config.
/// </summary>
public sealed record PityRequirement
{
    public required RequirementKind Kind { get; init; }
    public required MongoId ItemId { get; init; }
    public required int AmountRequired { get; init; }
    public required long SecondsSinceStarted { get; init; }
    public required int RaidsSinceStarted { get; init; }

    /// <summary>Quest conditions only: the condition id, used to read the player's
    /// progress counter so partially-completed hand-ins ask for the remainder.</summary>
    public string? ConditionId { get; init; }

    /// <summary>Quest conditions only: whether the hand-in demands found-in-raid items.</summary>
    public bool FoundInRaid { get; init; }
}
