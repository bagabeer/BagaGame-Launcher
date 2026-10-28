namespace BagaGame.Models;

/// <summary>
/// A game the user added by hand (a folder or an .exe that the automatic
/// scan does not cover). Persisted in <c>library.json</c>.
/// </summary>
public sealed class ManualGameEntry
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string InstallPath { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;
}
