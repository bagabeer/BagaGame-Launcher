namespace BagaGame.Services;

/// <summary>
/// Big Picture is laid out for 1080p and scaled by the screen height, so a 4K
/// television shows the same proportions instead of tiny controls. Ultrawide
/// screens share the same height and simply get more content in width.
/// </summary>
public static class UiScale
{
    public const double DesignHeight = 1080;

    public static double ForHeight(double height)
    {
        if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
        {
            return 1;
        }

        var scale = height / DesignHeight;

        // 1366x768 laptops stay readable, 4K scales exactly by two, and larger
        // panels keep growing instead of hitting an early ceiling.
        return Math.Round(Math.Min(2.5, Math.Max(0.75, scale)), 3);
    }
}
