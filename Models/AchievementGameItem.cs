namespace BagaGame.Models;

/// <summary>
/// One supported game shown in the Achievements tab. <see cref="Verified"/>
/// means the launcher actually found at least one earned achievement in the
/// local emulator/save files; a detected emulator alone is not proof.
/// </summary>
public sealed record AchievementGameItem(
    Game Game,
    string Platform,
    bool HasCatalog,
    bool Verified,
    int LocalCount,
    string StatusText)
{
    public bool CanOpen => HasCatalog;
}
