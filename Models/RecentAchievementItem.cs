using System;
using System.Globalization;
using BagaGame.Services;

namespace BagaGame.Models;

/// <summary>One compact row in the Big Picture "recent achievements" strip.</summary>
public sealed class RecentAchievementItem
{
    public RecentAchievementItem(SteamAchievement achievement)
    {
        Name = string.IsNullOrWhiteSpace(achievement.Name)
            ? Loc.T("Loc.BP.AchievementFallback")
            : achievement.Name;

        Description = achievement.Description;
        Icon = achievement.IconUrl;
        TimeText = FormatTime(achievement.UnlockTime);

        Tooltip = string.IsNullOrWhiteSpace(Description)
            ? Name
            : Name + Environment.NewLine + Description;
    }

    public string Name { get; }

    public string Description { get; }

    public string Icon { get; }

    public string TimeText { get; }

    public string Tooltip { get; }

    private static string FormatTime(DateTimeOffset? unlockTime)
    {
        if (unlockTime is null)
        {
            return Loc.T("Loc.BP.AchievementUnknownTime");
        }

        return unlockTime.Value
            .ToLocalTime()
            .ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
    }
}
