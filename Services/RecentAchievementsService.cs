using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BagaGame.Models;

namespace BagaGame.Services;

/// <summary>
/// Returns the latest unlocked achievements for the Big Picture hero. It uses
/// the same merge as the achievements tab, but skips global rarity: the hero
/// only needs a few rows and must stay light.
/// </summary>
public static class RecentAchievementsService
{
    public static async Task<IReadOnlyList<SteamAchievement>> GetAsync(
        Game game,
        int appId,
        CancellationToken cancellationToken)
    {
        if (appId <= 0)
        {
            return Array.Empty<SteamAchievement>();
        }

        var result = await SteamAchievementsService.Shared
            .GetAsync(appId, cancellationToken)
            .ConfigureAwait(false);

        var local = new LocalAchievementsService().Get(game, appId);

        var catalog = await SteamAchievementCatalogService.Shared
            .GetAsync(appId, SteamAchievementCatalogService.Language, cancellationToken)
            .ConfigureAwait(false);

        return AchievementMergeService
            .BuildAchievements(result, local, catalog)
            .Where(item => item.Unlocked)
            .OrderByDescending(item => item.UnlockTime ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
