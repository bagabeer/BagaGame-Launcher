namespace BagaGame.Models;

/// <summary>One row in the "Hidden games" list on the Settings page.</summary>
public sealed class HiddenGameItem
{
    public HiddenGameItem(string id, string title, string source)
    {
        Id = id;
        Title = title;
        Source = source;
    }

    public string Id { get; }

    public string Title { get; }

    public string Source { get; }
}
