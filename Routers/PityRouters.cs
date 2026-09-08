using PityLoot.Services;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Enums;
using System.Text.Json;
using SPTarkov.Server.Core.Utils;

namespace PityLoot.Routers;

// Every route here is a side-effect-only hook: the mod reads the request, updates pity,
// and hands back whatever core already produced. `output!` rather than `output ?? ""` is
// deliberate - passing through core's answer verbatim, including a null one, is the whole
// contract of a passthrough router.

/// <summary>
/// Game start. Establishes the pity baseline for the session without counting a raid,
/// which is also what makes the loot tables reflect this profile before the first load.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers + 1)]
public class PityGameStartRouter(JsonUtil jsonUtil, ModConfig config, PityService pity, ISptLogger<PityGameStartRouter> logger)
    : StaticRouter(jsonUtil,
    [
        new RouteAction<EmptyRequestData>(
            "/client/game/start",
            async (url, info, sessionId, output, cancellationToken) =>
            {
                if (config.Values.Enabled)
                {
                    await PityRouterGuard.RunAsync(logger, () => pity.OnPityChangedAsync(sessionId, false, cancellationToken));
                }

                return output!;
            }),
    ]);

/// <summary>
/// Raid end. The only place the raid counter moves.
///
/// <para>Whether a raid counts depends on config: by default only failed raids do, and
/// scav raids can be excluded. Surviving still triggers a recompute, because completing
/// quest items in-raid changes what is outstanding even when the counter does not move —
/// that was the last fix the 3.11 mod shipped and it is preserved here.</para>
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers + 1)]
public class PityRaidEndRouter(JsonUtil jsonUtil, ModConfig config, PityService pity, ISptLogger<PityRaidEndRouter> logger)
    : StaticRouter(jsonUtil,
    [
        new RouteAction<EndLocalRaidRequestData>(
            "/client/match/local/end",
            async (url, info, sessionId, output, cancellationToken) =>
            {
                if (!config.Values.Enabled)
                {
                    return output!;
                }

                var result = info?.Results?.Result;
                var failed = result is ExitStatus.KILLED or ExitStatus.MISSINGINACTION;
                var survived = result is ExitStatus.SURVIVED or ExitStatus.RUNNER;
                var isScav = string.Equals(info?.Results?.Profile?.Info?.Side, "Savage", StringComparison.OrdinalIgnoreCase);

                var countsAsRaid =
                    (failed || (!config.Values.OnlyIncreaseOnFailedRaids && survived)) &&
                    (!isScav || config.Values.IncludeScavRaids);

                await PityRouterGuard.RunAsync(logger, () => pity.OnPityChangedAsync(sessionId, countsAsRaid, cancellationToken));
                return output!;
            }),
    ]);

/// <summary>
/// Quest hand-ins and hideout upgrades. These finish requirements without ending a raid,
/// so pity has to be recomputed even though no raid counter moves.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers + 1)]
public class PityItemEventRouter(JsonUtil jsonUtil, ModConfig config, PityService pity, ISptLogger<PityItemEventRouter> logger)
    : StaticRouter(jsonUtil,
    [
        new RouteAction<ItemEventRouterRequest>(
            "/client/game/profile/items/moving",
            async (url, info, sessionId, output, cancellationToken) =>
            {
                if (!config.Values.Enabled)
                {
                    return output!;
                }

                // Data is a raw JsonElement list: the body shape varies per action, and
                // only the discriminator matters here.
                var relevant = info?.Data?.Any(body =>
                    body.ValueKind == JsonValueKind.Object &&
                    body.TryGetProperty("Action", out var action) &&
                    action.ValueKind == JsonValueKind.String &&
                    PityItemEventRouter.PityChangingActions.Contains(action.GetString()!)) ?? false;

                if (relevant)
                {
                    await PityRouterGuard.RunAsync(logger, () => pity.OnPityChangedAsync(sessionId, false, cancellationToken));
                }

                return output!;
            }),
    ])
{
    /// <summary>Client actions that finish or change a requirement without ending a raid.</summary>
    internal static readonly HashSet<string> PityChangingActions = new(StringComparer.Ordinal)
    {
        "QuestComplete", "QuestHandover",
        "HideoutImproveArea", "HideoutUpgrade", "HideoutUpgradeComplete",
    };
}

/// <summary>
/// Immediately before a raid loads. Bot inventory tables are not lazy-loaded, so unlike
/// location loot they are rewritten explicitly, at the last moment they can still matter.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers + 1)]
public class PityPreRaidRouter(JsonUtil jsonUtil, ModConfig config, PityService pity, ISptLogger<PityPreRaidRouter> logger)
    : StaticRouter(jsonUtil,
    [
        new RouteAction<EmptyRequestData>(
            "/client/raid/configuration",
            async (url, info, sessionId, output, cancellationToken) =>
            {
                if (config.Values.Enabled)
                {
                    await PityRouterGuard.RunAsync(logger, async () =>
                    {
                        await pity.RefreshAsync(sessionId, cancellationToken);
                        pity.RefreshBots();
                    });
                }

                return output!;
            }),
    ]);

/// <summary>
/// Every hook here is a side effect on a route the game needs to succeed. A failure in
/// pity bookkeeping must never turn into a failed request, so all of them run through
/// this.
/// </summary>
internal static class PityRouterGuard
{
    public static async Task RunAsync<T>(ISptLogger<T> logger, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Error($"[PityLoot] pity update failed (request unaffected): {ex}");
        }
    }
}
