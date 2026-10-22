namespace Spokes_Server.Components.UI;

/// <summary>
/// Pure helper methods for calculating media viewer zoom levels and 2D pan boundaries.
/// </summary>
public static class MediaZoomHelper
{
    public const double MinScale = 1.0;
    public const double MaxScale = 5.0;
    public const double DefaultStep = 0.25;

    public static double ClampScale(double scale) => Math.Clamp(scale, MinScale, MaxScale);

    public static int ToPercent(double scale) => (int)Math.Round(ClampScale(scale) * 100);

    public static double GetNextZoomIn(double currentScale)
    {
        double clamped = ClampScale(currentScale);
        if (clamped >= MaxScale) return MaxScale;
        double step = clamped >= 2.0 ? 0.5 : DefaultStep;
        return ClampScale(Math.Floor((clamped + step + 0.001) / step) * step);
    }

    public static double GetNextZoomOut(double currentScale)
    {
        double clamped = ClampScale(currentScale);
        if (clamped <= MinScale) return MinScale;
        double step = clamped > 2.0 ? 0.5 : DefaultStep;
        return ClampScale(Math.Ceiling((clamped - step - 0.001) / step) * step);
    }

    public static (double ClampedTx, double ClampedTy) CalculatePanBounds(
        double containerWidth,
        double containerHeight,
        double contentWidth,
        double contentHeight,
        double scale,
        double targetTx,
        double targetTy)
    {
        if (scale <= MinScale) return (0.0, 0.0);

        double visualWidth = contentWidth * scale;
        double visualHeight = contentHeight * scale;

        double maxX = visualWidth > containerWidth ? (visualWidth - containerWidth) / 2.0 : 0.0;
        double maxY = visualHeight > containerHeight ? (visualHeight - containerHeight) / 2.0 : 0.0;

        double clampedX = Math.Max(-maxX, Math.Min(maxX, targetTx));
        double clampedY = Math.Max(-maxY, Math.Min(maxY, targetTy));

        return (clampedX, clampedY);
    }
}
