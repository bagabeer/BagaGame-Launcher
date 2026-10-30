using System.Windows.Media;

namespace BagaGame.Models;

/// <summary>One cover found online for the cover picker.</summary>
public sealed class CoverCandidate
{
    public CoverCandidate(string url, string title, ImageSource preview)
    {
        Url = url;
        Title = title;
        Preview = preview;
    }

    public string Url { get; }

    public string Title { get; }

    /// <summary>Already decoded and frozen, so the grid scrolls smoothly.</summary>
    public ImageSource Preview { get; }
}
