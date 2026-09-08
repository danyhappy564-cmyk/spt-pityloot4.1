namespace PityLoot.Model;

/// <summary>
/// Per-profile pity state. The original stored every profile in one hand-rolled
/// <c>database/pityTracker.json</c> next to the mod; 4.1 has a first-class per-profile
/// mod data store (<c>ProfileDataService</c>), so this is saved through that instead —
/// one blob per profile, alongside the profile it belongs to, and cleaned up with it.
/// </summary>
public sealed record PityTracker
{
    /// <summary>Keyed by quest id.</summary>
    public Dictionary<string, QuestPity> Quests { get; set; } = new();

    /// <summary>Keyed by hideout area type (the numeric <c>HideoutAreas</c> value).</summary>
    public Dictionary<string, HideoutPity> Hideout { get; set; } = new();
}

public sealed record QuestPity
{
    public int RaidsSinceStarted { get; set; }
}

public sealed record HideoutPity
{
    /// <summary>The upgrade level this pity is being accumulated for. When the next
    /// available upgrade is higher than this, the player upgraded and the counters reset.</summary>
    public int CurrentLevel { get; set; }

    /// <summary>Unix milliseconds at which this upgrade became available.</summary>
    public long TimeAvailable { get; set; }

    public int RaidsSinceStarted { get; set; }
}
