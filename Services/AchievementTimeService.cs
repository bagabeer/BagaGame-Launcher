using System;
using System.Collections.Generic;
using BagaGame.Models;

namespace BagaGame.Services;

/// <summary>
/// Keeps one unlock time for every achievement. Steam and some emulator files
/// provide the real moment; for the rest the launcher records the first time
/// it sees the achievement unlocked, so both the library and Big Picture can
/// show a date instead of "recently".
/// </summary>
public static class AchievementTimeService
{
    public static void Apply(
        Game game,
        IEnumerable<SteamAchievement> achievements,
        LibraryStore store)
    {
        var stored = store.GetAchievementUnlockTimes(game.Id);
        var missing = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.Now;

        foreach (var achievement in achievements)
        {
            if (!achievement.Unlocked || achievement.UnlockTime is not null)
            {
                continue;
            }

            if (TryGetStored(stored, achievement, out var time))
            {
                achievement.UnlockTime = time;
                continue;
            }

            achievement.UnlockTime = now;

            var key = string.IsNullOrWhiteSpace(achievement.ApiName)
                ? achievement.Name
                : achievement.ApiName;

            if (!string.IsNullOrWhiteSpace(key))
            {
                missing[key] = now;
            }

            // A local display title can be the only stable key for an emulator.
            if (!string.IsNullOrWhiteSpace(achievement.Name) &&
                !string.Equals(achievement.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                missing[achievement.Name] = now;
            }
        }

        if (missing.Count > 0)
        {
            store.RecordAchievementUnlocks(game.Id, missing);
        }
    }

    private static bool TryGetStored(
        IReadOnlyDictionary<string, long> stored,
        SteamAchievement achievement,
        out DateTimeOffset time)
    {
        if (!string.IsNullOrWhiteSpace(achievement.ApiName) &&
            stored.TryGetValue(achievement.ApiName, out var apiSeconds) &&
            LocalAchievements.FromTimestamp(apiSeconds) is { } apiTime)
        {
            time = apiTime;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(achievement.Name) &&
            stored.TryGetValue(achievement.Name, out var nameSeconds) &&
            LocalAchievements.FromTimestamp(nameSeconds) is { } nameTime)
        {
            time = nameTime;
            return true;
        }

        time = default;
        return false;
    }
}
