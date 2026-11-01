namespace BagaGame.Services;

/// <summary>
/// Store description and genres shown in the Big Picture banner. Cached on
/// disk so the banner does not wait for the network on every selection.
/// </summary>
public sealed class GameMetadata
{
    public string? Description { get; set; }

    /// <summary>Full store description shown in the Big Picture details panel.</summary>
    public string? Details { get; set; }

    public List<string> Genres { get; set; } = new();
}
