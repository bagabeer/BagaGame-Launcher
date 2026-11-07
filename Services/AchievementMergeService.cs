using System;
using System.Collections.Generic;
using System.Linq;

namespace BagaGame.Services;

/// <summary>
/// Combines the store catalogue, the Steam profile and the local emulator
/// files into one achievement list. The achievements tab and the compact Big
/// Picture strip use the same merge, so names and icons never disagree.
/// </summary>
public static class AchievementMergeService
{
    public static List<SteamAchievement> BuildAchievements(
        SteamAchievementsResult result,
        LocalAchievements local,
        IReadOnlyList<SteamAchievement> catalog)
    {
        var byApi = result.Achievements
            .Where(item => !string.IsNullOrWhiteSpace(item.ApiName))
            .GroupBy(item => item.ApiName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var hasCatalog = catalog.Count > 0;
        var source = hasCatalog ? catalog : result.Achievements;
        var merged = new List<SteamAchievement>();

        foreach (var item in source)
        {
            // Always copy: the catalogue is a shared cache, and the caller may
            // attach a first-seen unlock time to the result.
            var baseItem = new SteamAchievement
            {
                ApiName = item.ApiName,
                Name = item.Name,
                Description = item.Description,
                Unlocked = item.Unlocked,
                IconOpen = item.IconOpen,
                IconLocked = item.IconLocked,
                IconUrl = item.IconUrl,
                UnlockTime = item.UnlockTime,
            };

            if (hasCatalog && byApi.TryGetValue(item.ApiName, out var steam))
            {
                baseItem = new SteamAchievement
                {
                    ApiName = item.ApiName,
                    Name = item.Name,
                    Description = item.Description,
                    Unlocked = steam.Unlocked,
                    IconOpen = item.IconOpen,
                    IconLocked = item.IconLocked,
                    IconUrl = steam.Unlocked && !string.IsNullOrWhiteSpace(item.IconOpen)
                        ? item.IconOpen
                        : item.IconLocked,
                    UnlockTime = steam.UnlockTime,
                };
            }

            var mergedItem = Merge(baseItem, local);

            if (mergedItem.UnlockTime is null)
            {
                mergedItem.UnlockTime = local.GetUnlockTime(mergedItem.ApiName, mergedItem.Name);
            }

            merged.Add(mergedItem);
        }

        return merged;
    }

    /// <summary>Marks an achievement unlocked when a local emulator file says so.</summary>
    public static SteamAchievement Merge(SteamAchievement achievement, LocalAchievements local)
    {
        if (achievement.Unlocked ||
            string.IsNullOrWhiteSpace(achievement.ApiName) ||
            !local.IsUnlocked(achievement.ApiName, achievement.Name))
        {
            return achievement;
        }

        return new SteamAchievement
        {
            ApiName = achievement.ApiName,
            Name = achievement.Name,
            Description = achievement.Description,
            Unlocked = true,
            IconOpen = achievement.IconOpen,
            IconLocked = achievement.IconLocked,
            IconUrl = string.IsNullOrWhiteSpace(achievement.IconOpen)
                ? achievement.IconUrl
                : achievement.IconOpen,
            UnlockTime = achievement.UnlockTime,
        };
    }
}
