namespace BagaGame.Models;

/// <summary>One chip in the Big Picture genre bar ("All genres", "Action (12)").</summary>
public sealed class GenreFilter
{
    public GenreFilter(string name, string? genre, int count, bool showCount)
    {
        Name = name;
        Genre = genre;
        Count = count;
        ShowCount = showCount;
    }

    public string Name { get; }

    /// <summary>Canonical genre name, or null for "All genres".</summary>
    public string? Genre { get; }

    public int Count { get; }

    public bool ShowCount { get; }

    public string Label => ShowCount ? $"{Name} ({Count})" : Name;

    public override string ToString() => Label;
}
