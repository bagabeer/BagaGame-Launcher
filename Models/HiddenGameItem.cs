namespace BagaGame.Models;

/// <summary>One row in the "Hidden games" list on the Settings page.</summary>
public sealed class HiddenGameItem
{
    public HiddenGameItem(string id, string title, string details)
    {
        Id = id;
        Title = title;
        Details = details;
    }

    public string Id { get; }

    public string Title { get; }

    /// <summary>Source and path, formatted like the list view on the library page.</summary>
    public string Details { get; }
}
