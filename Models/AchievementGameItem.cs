namespace BagaGame.Models;

/// <summary>
/// One supported game shown in the Achievements tab. <see cref="Verified"/>
/// means the launcher actually showed an achievement toast for it; a detected
/// emulator and old local progress alone are not proof.
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
