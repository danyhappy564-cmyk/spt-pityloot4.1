using System.Collections.Concurrent;
using PityLoot.Model;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services.Modding;

namespace PityLoot.Services;

/// <summary>
/// Loads and saves the per-profile <see cref="PityTracker"/> through SPT 4.1's
/// <c>ProfileDataService</c>, and holds the last-read value in memory so the hot path
/// (rebuilding loot tables) never touches disk.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class PityTrackerStore(ProfileDataService profileData, ISptLogger<PityTrackerStore> logger)
{
    private const string ModKey = "PityLoot";

    private readonly ConcurrentDictionary<MongoId, PityTracker> _cache = new();

    public PityTracker Get(MongoId profileId) =>
        _cache.TryGetValue(profileId, out var tracker) ? tracker : new PityTracker();

    public async Task<PityTracker> LoadAsync(MongoId profileId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(profileId, out var cached))
        {
            return cached;
        }

        PityTracker tracker;
        try
        {
            tracker = await profileData.GetProfileDataAsync<PityTracker>(profileId, ModKey, cancellationToken)
                      ?? new PityTracker();
        }
        catch (Exception ex)
        {
            // A corrupt or unreadable blob must not stop the server booting. Starting from
            // zero pity is the safe direction - the player loses accumulated pity, they do
            // not lose anything from their profile.
            logger.Warning($"[PityLoot] could not read stored pity for {profileId}, starting fresh: {ex.Message}");
            tracker = new PityTracker();
        }

        return _cache[profileId] = tracker;
    }

    public async Task SaveAsync(MongoId profileId, PityTracker tracker, CancellationToken cancellationToken = default)
    {
        _cache[profileId] = tracker;
        try
        {
            await profileData.SaveProfileDataAsync(profileId, ModKey, tracker, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.Warning($"[PityLoot] could not persist pity for {profileId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Recomputes the tracker from the profile's current quest list and the hideout
    /// upgrades it can start, optionally counting this as one more raid.
    ///
    /// <para>Quests that are no longer <c>Started</c> and hideout areas that are no longer
    /// upgradeable simply do not make it into the new tracker, which is how completing
    /// something resets its pity.</para>
    /// </summary>
    public PityTracker Rebuild(PityTracker previous, PmcData pmc, IReadOnlyList<HideoutUpgrade> upgrades, bool incrementRaidCount)
    {
        var raidIncrease = incrementRaidCount ? 1 : 0;
        var next = new PityTracker();

        foreach (var quest in pmc.Quests ?? [])
        {
            if (quest.Status != QuestStatusEnum.Started)
            {
                continue;
            }

            var key = quest.QId.ToString();
            previous.Quests.TryGetValue(key, out var old);
            next.Quests[key] = new QuestPity { RaidsSinceStarted = (old?.RaidsSinceStarted ?? 0) + raidIncrease };
        }

        foreach (var upgrade in upgrades)
        {
            var key = ((int)upgrade.Area).ToString();
            if (!previous.Hideout.TryGetValue(key, out var old))
            {
                old = new HideoutPity { CurrentLevel = 0, RaidsSinceStarted = 0, TimeAvailable = NowMs() };
            }

            // A higher next-upgrade than the one we were tracking means the player
            // completed the one we were counting for, so the pity starts over.
            next.Hideout[key] = upgrade.Level > old.CurrentLevel
                ? new HideoutPity { CurrentLevel = upgrade.Level, RaidsSinceStarted = raidIncrease, TimeAvailable = NowMs() }
                : new HideoutPity
                {
                    CurrentLevel = old.CurrentLevel,
                    TimeAvailable = old.TimeAvailable,
                    RaidsSinceStarted = old.RaidsSinceStarted + raidIncrease,
                };
        }

        return next;
    }

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
