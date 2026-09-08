using System.Text.Json.Serialization;

namespace PityLoot.Model;

/// <summary>
/// Mirrors the original <c>config/config.json</c> one-for-one so an existing config file
/// keeps working. Property names are matched case-insensitively by the loader, so the
/// original camelCase keys bind to these PascalCase properties unchanged.
/// </summary>
public record PityConfig
{
    public bool Enabled { get; set; } = true;
    public bool Debug { get; set; }

    /// <summary>Logs every single probability rewrite. Hundreds of thousands of lines.</summary>
    public bool Trace { get; set; }

    public bool AppliesToQuests { get; set; } = true;
    public bool IncludeGunsmith { get; set; } = true;
    public bool AppliesToHideout { get; set; } = true;

    /// <summary>When true two tasks needing the same item add their pity together;
    /// when false the larger of the two is used.</summary>
    public bool IncreasesStack { get; set; } = true;

    public bool IncludeScavRaids { get; set; } = true;
    public bool OnlyIncreaseOnFailedRaids { get; set; } = true;
    public bool IncludeKeys { get; set; } = true;

    /// <summary>Extra multiplier on top of the normal one for quest keys.</summary>
    public double KeysAdditionalMultiplier { get; set; } = 2.5;

    public double MaxDropRateMultiplier { get; set; } = 10;

    /// <summary><c>raid</c> or <c>time</c>.</summary>
    public string DropRateIncreaseType { get; set; } = "raid";

    public double DropRateIncreasePerRaid { get; set; } = 0.25;
    public double DropRateIncreasePerHour { get; set; } = 0.05;

    public bool AppliesToWishlist { get; set; }
    public WishlistMultipliers WishlistMultipliers { get; set; } = new();

    public bool ExcludeCollector { get; set; }

    [JsonIgnore]
    public bool UsesRaidCounter => !string.Equals(DropRateIncreaseType, "time", StringComparison.OrdinalIgnoreCase);
}

public record WishlistMultipliers
{
    public double Tasks { get; set; } = 5.0;
    public double Equipment { get; set; } = 5.0;
    public double Barter { get; set; } = 5.0;
    public double Hideout { get; set; } = 5.0;
    public double Other { get; set; } = 5.0;

    /// <summary>The client stores a wishlist category as an int; 0..4 map in this order.</summary>
    public double ForCategory(int category) => category switch
    {
        0 => Tasks,
        1 => Hideout,
        2 => Barter,
        3 => Equipment,
        _ => Other,
    };
}
